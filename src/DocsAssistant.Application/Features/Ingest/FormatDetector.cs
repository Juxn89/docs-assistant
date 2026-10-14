using System.Text;
using DocsAssistant.Application.Common;
using DocsAssistant.Domain.Documents;

namespace DocsAssistant.Application.Features.Ingest;

/// <summary>
/// Decides the format from the bytes first and the name/media type second. A file called "a.pdf"
/// that is not a PDF is rejected here rather than failing three retries later in the worker.
/// </summary>
public static class FormatDetector
{
	private const string DocxMediaType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

	private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();

	/// <summary>Every OOXML document is a ZIP archive: local file header signature.</summary>
	private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];

	private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

	public static Result<DocumentSourceType> Detect(ReadOnlySpan<byte> content, string? fileName, string? mediaType)
	{
		if (content.IsEmpty)
		{
			return Error.Validation("document.empty", "The document is empty.");
		}

		var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
		var media = (mediaType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();

		if (content.StartsWith(PdfMagic))
		{
			return DocumentSourceType.Pdf;
		}

		if (content.StartsWith(ZipMagic))
		{
			return extension == ".docx" || media == DocxMediaType
				? DocumentSourceType.Docx
				: Error.Validation("document.unsupported_archive", "Only .docx archives are supported.");
		}

		if (!IsUtf8Text(content))
		{
			return Error.Validation(
				"document.unsupported_format",
				"Supported formats: PDF, DOCX, Markdown, plain text and HTML (UTF-8).");
		}

		if (media == "text/html" || extension is ".html" or ".htm" || LooksLikeHtml(content))
		{
			return DocumentSourceType.Html;
		}

		return media == "text/markdown" || extension is ".md" or ".markdown"
			? DocumentSourceType.Markdown
			: DocumentSourceType.PlainText;
	}

	private static bool IsUtf8Text(ReadOnlySpan<byte> content)
	{
		try
		{
			var text = StrictUtf8.GetString(content);
			return !text.Contains('\0', StringComparison.Ordinal);
		}
		catch (DecoderFallbackException)
		{
			return false;
		}
	}

	private static bool LooksLikeHtml(ReadOnlySpan<byte> content)
	{
		const int SniffBytes = 512;
		var head = Encoding.UTF8.GetString(content[..Math.Min(content.Length, SniffBytes)]).TrimStart().ToLowerInvariant();
		return head.StartsWith("<!doctype html", StringComparison.Ordinal) || head.StartsWith("<html", StringComparison.Ordinal);
	}
}
