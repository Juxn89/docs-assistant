using DocsAssistant.Application.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DocsAssistant.Infrastructure.Ai.Embeddings;

/// <summary>
/// multilingual-e5-small running in-process on ONNX Runtime: tokenize, run the encoder, mean-pool the
/// last hidden state, L2-normalize. That is the procedure from the model card, and the integration
/// tests check this implementation against vectors produced by it in Python. Normalized output means
/// cosine similarity is a plain dot product.
///
/// <para><b>One sequence per inference, never a padded batch.</b> The model is dynamically quantized
/// to int8: activation scales are computed over the whole input tensor, padding included, so the same
/// passage embedded next to a longer neighbour comes out different (cosine 0.994 against itself,
/// measured in Python on 14 Oct 2026). An index whose vectors depend on what else was in the batch is
/// not reproducible. At the volumes of a support knowledge base, single-sequence inference costs
/// milliseconds per chunk and buys determinism.</para>
///
/// The model loads lazily on first use, so an API that never ingests or chats never pays for it.
/// </summary>
internal enum EmbeddingRole
{
	Query = 0,
	Passage = 1,
}

internal sealed class OnnxEmbedder : IEmbedder, IDisposable
{
	private const string InputIds = "input_ids";
	private const string AttentionMask = "attention_mask";
	private const string TokenTypeIds = "token_type_ids";

	private readonly EmbeddingModelOptions _options;
	private readonly ModelFileProvider _files;
	private readonly SemaphoreSlim _loadLock = new(1, 1);
	private (InferenceSession Session, XlmRobertaTokenizer Tokenizer)? _model;

	public OnnxEmbedder(IOptions<EmbeddingModelOptions> options, ModelFileProvider files)
	{
		ArgumentNullException.ThrowIfNull(options);
		_options = options.Value;
		_files = files;
	}

	public int Dimensions => _options.Dimensions;

	public async Task<IReadOnlyList<float[]>> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(passages);
		if (passages.Count == 0)
		{
			return [];
		}

		var model = await LoadAsync(cancellationToken).ConfigureAwait(false);
		return [.. passages.Select(p => EmbedOne(model, Encode(model.Tokenizer, p, EmbeddingRole.Passage)))];
	}

	public async Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken)
	{
		var model = await LoadAsync(cancellationToken).ConfigureAwait(false);
		return EmbedOne(model, Encode(model.Tokenizer, query, EmbeddingRole.Query));
	}

	public void Dispose()
	{
		_model?.Session.Dispose();
		_loadLock.Dispose();
	}

	/// <summary>
	/// The exact token ids the model receives, role prefix included. Exposed so tests can pin them
	/// against Hugging Face's tokenizer: unlike the vectors, ids do not vary with the CPU (ADR-0003), so a
	/// missing "query: " prefix — which moves the vector less than a CPU change does — is still caught.
	/// </summary>
	internal long[] Encode(XlmRobertaTokenizer tokenizer, string text, EmbeddingRole role) =>
		tokenizer.Encode((role == EmbeddingRole.Query ? _options.QueryPrefix : _options.PassagePrefix) + text, _options.MaxSequenceLength);

	private static float[] EmbedOne((InferenceSession Session, XlmRobertaTokenizer Tokenizer) model, long[] ids)
	{
		int[] shape = [1, ids.Length];

		var inputIds = new DenseTensor<long>(ids, shape);
		var mask = new DenseTensor<long>(Enumerable.Repeat(1L, ids.Length).ToArray(), shape);
		var tokenTypes = new DenseTensor<long>(new long[ids.Length], shape);

		using var outputs = model.Session.Run(
		[
			NamedOnnxValue.CreateFromTensor(InputIds, inputIds),
			NamedOnnxValue.CreateFromTensor(AttentionMask, mask),
			NamedOnnxValue.CreateFromTensor(TokenTypeIds, tokenTypes),
		]);
		var hidden = outputs[0].AsTensor<float>();
		var dimensions = hidden.Dimensions[2];

		// Mean pooling over every token (there is no padding), then L2 normalization.
		var vector = new float[dimensions];
		for (var token = 0; token < ids.Length; token++)
		{
			for (var d = 0; d < dimensions; d++)
			{
				vector[d] += hidden[0, token, d];
			}
		}

		var norm = 0.0;
		for (var d = 0; d < dimensions; d++)
		{
			vector[d] /= ids.Length;
			norm += vector[d] * vector[d];
		}

		var scale = (float)(1.0 / Math.Sqrt(norm));
		for (var d = 0; d < dimensions; d++)
		{
			vector[d] *= scale;
		}

		return vector;
	}

	private async Task<(InferenceSession Session, XlmRobertaTokenizer Tokenizer)> LoadAsync(CancellationToken cancellationToken)
	{
		if (_model is { } loaded)
		{
			return loaded;
		}

		await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_model is { } raced)
			{
				return raced;
			}

			var directory = _options.ResolveCacheDirectory();
			var modelPath = await _files.EnsureAsync(_options.ModelUrl, _options.ModelSha256, directory, cancellationToken).ConfigureAwait(false);
			var tokenizerPath = await _files.EnsureAsync(_options.TokenizerUrl, _options.TokenizerSha256, directory, cancellationToken).ConfigureAwait(false);

			XlmRobertaTokenizer tokenizer;
			await using (var stream = File.OpenRead(tokenizerPath))
			{
				tokenizer = new XlmRobertaTokenizer(stream);
			}

			using var sessionOptions = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
			var session = new InferenceSession(modelPath, sessionOptions);
			var actual = session.OutputMetadata.Values.First().Dimensions[^1];
			if (actual != _options.Dimensions)
			{
				session.Dispose();
				throw new InvalidOperationException(
					$"The embedding model produces {actual} dimensions but {_options.Dimensions} are configured.");
			}

			_model = (session, tokenizer);
			return _model.Value;
		}
		finally
		{
			_loadLock.Release();
		}
	}
}
