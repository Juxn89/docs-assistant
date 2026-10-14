using System.Text.Json;
using DocsAssistant.Infrastructure.Ai.Embeddings;

namespace DocsAssistant.Integration.Tests;

/// <summary>
/// The C# port of tokenization and pooling is checked against the reference implementation: token ids
/// from Hugging Face's tokenizer and vectors from ONNX Runtime in Python, both generated from the
/// same pinned model files (Fixtures/*.json). A wrong offset or pooling bug produces plausible-looking
/// vectors and no error — only a comparison like this catches it.
/// </summary>
[Collection(EmbedderCollection.Name)]
public sealed class EmbedderTests(EmbedderFixture model)
{
	private const string PassagePrefix = "passage: ";
	private const string QueryPrefix = "query: ";

	/// <summary>
	/// Agreement with the Python reference, which was generated on a CPU with AVX512-VNNI. The int8
	/// model runs different ONNX Runtime kernels per instruction set, deterministically: measured on
	/// 2026-10-14, a VNNI CPU (laptop i7-1165G7, GitHub's AMD EPYC 9V45) gives ≥ 0.9999, an AVX2-only
	/// CPU (GitHub's AMD EPYC 7763) gives 0.9935 — every time. Injected bugs land far lower (no fairseq
	/// offset 0.777, CLS pooling 0.864). A missing query prefix does not, which is why
	/// <see cref="Embedder_FeedsTheModelThePrefixedTokens"/> checks token ids instead. See ADR-0003.
	/// </summary>
	private const double ReferenceAgreement = 0.99;

	/// <summary>Same machine, same input: must be identical up to float rounding.</summary>
	private const double SameMachineAgreement = 0.99999;

	public static TheoryData<string, long[]> TokenCases()
	{
		var data = new TheoryData<string, long[]>();
		foreach (var item in LoadFixture<TokenCase[]>("e5-token-ids.json"))
		{
			data.Add(item.Text, item.Ids);
		}

		return data;
	}

	[Theory]
	[MemberData(nameof(TokenCases))]
	public void Tokenizer_MatchesHuggingFace(string text, long[] expected)
	{
		var actual = model.Tokenizer.Encode(text, model.Options.MaxSequenceLength);

		Assert.Equal(expected, actual);
	}

	[Theory]
	[InlineData("query: ", true)]
	[InlineData("passage: ", false)]
	public void Embedder_FeedsTheModelThePrefixedTokens(string prefix, bool isQuery)
	{
		var role = isQuery ? EmbeddingRole.Query : EmbeddingRole.Passage;
		var reference = LoadFixture<TokenCase[]>("e5-token-ids.json").First(c => c.Text.StartsWith(prefix, StringComparison.Ordinal));

		var ids = model.Embedder.Encode(model.Tokenizer, reference.Text[prefix.Length..], role);

		Assert.Equal(reference.Ids, ids);
	}

	[Fact]
	public void Tokenizer_TruncatesButKeepsTheSpecialTokens()
	{
		var ids = model.Tokenizer.Encode(string.Join(' ', Enumerable.Repeat("palabra", 2000)), maxLength: 16);

		Assert.Equal(16, ids.Length);
		Assert.Equal(0, ids[0]);
		Assert.Equal(2, ids[^1]);
	}

	[Fact]
	public async Task Embeddings_MatchThePythonReference()
	{
		foreach (var reference in LoadFixture<EmbeddingCase[]>("e5-embeddings.json"))
		{
			var actual = reference.Text.StartsWith(QueryPrefix, StringComparison.Ordinal)
				? await model.Embedder.EmbedQueryAsync(reference.Text[QueryPrefix.Length..], CancellationToken.None)
				: (await model.Embedder.EmbedPassagesAsync([reference.Text[PassagePrefix.Length..]], CancellationToken.None))[0];

			Assert.True(
				Cosine(actual, reference.Embedding) >= ReferenceAgreement,
				$"'{reference.Text}': cosine {Cosine(actual, reference.Embedding):F6} with the Python reference.");
		}
	}

	[Fact]
	public async Task Embeddings_AreUnitLengthAndBatchIndependent()
	{
		string[] passages = ["Corto.", "Un pasaje bastante más largo que el anterior, para forzar padding en el lote."];

		var batched = await model.Embedder.EmbedPassagesAsync(passages, CancellationToken.None);
		var single = await model.Embedder.EmbedPassagesAsync([passages[0]], CancellationToken.None);

		Assert.All(batched, v => Assert.Equal(1.0, Math.Sqrt(v.Sum(x => (double)x * x)), precision: 4));
		// Padding inside a batch must not change a passage's vector.
		Assert.True(Cosine(batched[0], single[0]) >= SameMachineAgreement);
	}

	[Fact]
	public async Task CrossLingual_QuestionInSpanish_FindsTheEnglishPassage()
	{
		var query = await model.Embedder.EmbedQueryAsync("¿Cuántos días tengo para devolver un producto?", CancellationToken.None);
		var passages = await model.Embedder.EmbedPassagesAsync(
			["You can return any product within 30 days of delivery.", "Our offices are open Monday to Friday."],
			CancellationToken.None);

		Assert.True(Cosine(query, passages[0]) > Cosine(query, passages[1]));
	}

	private static double Cosine(float[] a, float[] b)
	{
		double dot = 0, na = 0, nb = 0;
		for (var i = 0; i < a.Length; i++)
		{
			dot += a[i] * b[i];
			na += a[i] * a[i];
			nb += b[i] * b[i];
		}

		return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
	}

	private static T LoadFixture<T>(string name) =>
		JsonSerializer.Deserialize<T>(
			File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)),
			new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

	private sealed record TokenCase(string Text, long[] Ids);

	private sealed record EmbeddingCase(string Text, float[] Embedding);
}
