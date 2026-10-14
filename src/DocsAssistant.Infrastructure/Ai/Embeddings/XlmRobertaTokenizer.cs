using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;

namespace DocsAssistant.Infrastructure.Ai.Embeddings;

/// <summary>
/// XLM-RoBERTa tokenization on top of the raw SentencePiece model.
///
/// The model's vocabulary is not the SentencePiece vocabulary: fairseq reserved four ids at the start
/// (&lt;s&gt;=0, &lt;pad&gt;=1, &lt;/s&gt;=2, &lt;unk&gt;=3) and shifted every SentencePiece piece by one.
/// Feeding raw SentencePiece ids to the ONNX model does not fail — it silently produces embeddings of
/// the wrong tokens. The integration tests compare against ids produced by Hugging Face's tokenizer.
/// </summary>
internal sealed partial class XlmRobertaTokenizer
{
	public const long BeginOfSequenceId = 0;
	public const long PaddingId = 1;
	public const long EndOfSequenceId = 2;
	public const long UnknownId = 3;

	/// <summary>SentencePiece's own unknown-piece id.</summary>
	private const int SentencePieceUnknownId = 0;

	/// <summary>fairseq shifted the SentencePiece vocabulary by one to make room for its special tokens.</summary>
	private const int FairseqOffset = 1;

	private readonly SentencePieceTokenizer _sentencePiece;

	public XlmRobertaTokenizer(Stream sentencePieceModel)
	{
		_sentencePiece = SentencePieceTokenizer.Create(
			sentencePieceModel,
			addBeginningOfSentence: false,
			addEndOfSentence: false);
	}

	/// <summary>
	/// &lt;s&gt; pieces… &lt;/s&gt;, truncated so the whole sequence fits in <paramref name="maxLength"/>.
	/// Whitespace is collapsed and trimmed first: Hugging Face's tokenizer turns trailing spaces into an
	/// extra "▁" token that carries no meaning, and normalizing on both sides keeps the two identical.
	/// </summary>
	public long[] Encode(string text, int maxLength)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);

		var normalized = Whitespace().Replace(text ?? string.Empty, " ").Trim();
		var pieces = normalized.Length == 0 ? [] : _sentencePiece.EncodeToIds(normalized);
		var bodyLength = Math.Min(pieces.Count, maxLength - 2);

		var ids = new long[bodyLength + 2];
		ids[0] = BeginOfSequenceId;
		for (var i = 0; i < bodyLength; i++)
		{
			var piece = pieces[i];
			ids[i + 1] = piece == SentencePieceUnknownId ? UnknownId : piece + FairseqOffset;
		}

		ids[^1] = EndOfSequenceId;
		return ids;
	}

	[GeneratedRegex(@"\s+")]
	private static partial Regex Whitespace();
}
