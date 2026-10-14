using System.ComponentModel.DataAnnotations;

namespace DocsAssistant.Infrastructure.Ai.Embeddings;

/// <summary>
/// Where the embedding model comes from and how it is used. The defaults pin intfloat/multilingual-e5-small
/// (MIT) to an exact Hugging Face revision and verify every file by SHA-256: a model that changes
/// underneath makes yesterday's index and today's queries incomparable, silently.
/// Verified against huggingface.co on 14 Oct 2026.
/// </summary>
public sealed class EmbeddingModelOptions
{
	public const string SectionName = "Embeddings";

	private const string Repository = "https://huggingface.co/intfloat/multilingual-e5-small/resolve/614241f622f53c4eeff9890bdc4f31cfecc418b3";

	/// <summary>Dynamic int8 quantization of the official ONNX export: 118 MB instead of 470 MB.</summary>
	[Required]
	public Uri ModelUrl { get; set; } = new($"{Repository}/onnx/model_qint8_avx512_vnni.onnx");

	[Required, RegularExpression("^[0-9a-f]{64}$")]
	public string ModelSha256 { get; set; } = "dd476dd0c2514e9b9be83aeb3853fac0763e0bdf4a71645407587d77c48a2d88";

	[Required]
	public Uri TokenizerUrl { get; set; } = new($"{Repository}/onnx/sentencepiece.bpe.model");

	[Required, RegularExpression("^[0-9a-f]{64}$")]
	public string TokenizerSha256 { get; set; } = "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865";

	/// <summary>Downloaded files land here, named by hash. Defaults to the user's local app data.</summary>
	public string? CacheDirectory { get; set; }

	[Range(1, 4096)]
	public int Dimensions { get; set; } = 384;

	/// <summary>XLM-RoBERTa's position limit, special tokens included.</summary>
	[Range(8, 512)]
	public int MaxSequenceLength { get; set; } = 512;

	/// <summary>e5 is trained with these prefixes; leaving them out measurably hurts retrieval.</summary>
	[Required(AllowEmptyStrings = true)]
	public string QueryPrefix { get; set; } = "query: ";

	[Required(AllowEmptyStrings = true)]
	public string PassagePrefix { get; set; } = "passage: ";

	[Range(typeof(TimeSpan), "00:00:10", "00:30:00")]
	public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromMinutes(10);

	public string ResolveCacheDirectory() =>
		!string.IsNullOrWhiteSpace(CacheDirectory)
			? CacheDirectory
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "docs-assistant", "models");
}
