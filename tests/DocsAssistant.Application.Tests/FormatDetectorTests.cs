using System.Text;
using DocsAssistant.Application.Features.Ingest;
using DocsAssistant.Domain.Documents;

namespace DocsAssistant.Application.Tests;

public sealed class FormatDetectorTests
{
	[Theory]
	[InlineData("%PDF-1.7 ...", "anything.bin", null, DocumentSourceType.Pdf)]
	[InlineData("# Title\ntext", "notes.md", null, DocumentSourceType.Markdown)]
	[InlineData("plain", "notes.txt", null, DocumentSourceType.PlainText)]
	[InlineData("plain", null, "text/markdown; charset=utf-8", DocumentSourceType.Markdown)]
	[InlineData("  <!DOCTYPE html><html></html>", "page", null, DocumentSourceType.Html)]
	[InlineData("<p>hi</p>", null, "text/html", DocumentSourceType.Html)]
	public void Detect_ByContentThenNameThenMediaType(string content, string? fileName, string? mediaType, DocumentSourceType expected)
	{
		var result = FormatDetector.Detect(Encoding.UTF8.GetBytes(content), fileName, mediaType);

		Assert.True(result.IsSuccess, result.Error?.Message);
		Assert.Equal(expected, result.Value);
	}

	[Fact]
	public void Detect_PdfExtensionWithoutPdfBytes_IsNotTrustedAsPdf()
	{
		var result = FormatDetector.Detect("just text"u8, "fake.pdf", "application/pdf");

		Assert.Equal(DocumentSourceType.PlainText, result.Value);
	}

	[Fact]
	public void Detect_ZipThatIsNotDocx_IsRejected()
	{
		var result = FormatDetector.Detect([0x50, 0x4B, 0x03, 0x04, 0, 0], "sheet.xlsx", null);

		Assert.Equal("document.unsupported_archive", result.Error?.Code);
	}

	[Fact]
	public void Detect_ZipNamedDocx_IsDocx() =>
		Assert.Equal(DocumentSourceType.Docx, FormatDetector.Detect([0x50, 0x4B, 0x03, 0x04, 0, 0], "cv.docx", null).Value);

	[Fact]
	public void Detect_Binary_IsRejected() =>
		Assert.Equal("document.unsupported_format", FormatDetector.Detect([0xFF, 0xD8, 0xFF, 0xE0, 0x00], "photo.jpg", null).Error?.Code);

	[Fact]
	public void Detect_Empty_IsRejected() =>
		Assert.Equal("document.empty", FormatDetector.Detect([], "a.txt", null).Error?.Code);
}
