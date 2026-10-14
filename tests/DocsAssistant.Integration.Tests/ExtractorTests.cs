using System.Text;
using DocsAssistant.Domain.Documents;
using DocsAssistant.Infrastructure.Extraction;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace DocsAssistant.Integration.Tests;

/// <summary>Real files built in memory with the same libraries' writers — no binary fixtures to rot.</summary>
public sealed class ExtractorTests
{
	[Fact]
	public void Pdf_EachPageBecomesASectionNamedByItsNumber()
	{
		var pdf = BuildPdf("Returns are accepted within 30 days.", "Shipping is free over 50 USD.");

		var extracted = new PdfTextExtractor().Extract(pdf);

		Assert.Collection(
			extracted.Blocks,
			b => Assert.Equal((TextBlockKind.Heading, "p. 1"), (b.Kind, b.Text)),
			b => Assert.Contains("30 days", b.Text, StringComparison.Ordinal),
			b => Assert.Equal((TextBlockKind.Heading, "p. 2"), (b.Kind, b.Text)),
			b => Assert.Contains("50 USD", b.Text, StringComparison.Ordinal));
	}

	[Fact]
	public void Docx_HeadingStylesBecomeStructure_AndTablesAreRead()
	{
		var docx = BuildDocx();

		var extracted = new DocxTextExtractor().Extract(docx);

		Assert.Equal("Store policies", extracted.Title);
		Assert.Contains(extracted.Blocks, b => b is { Kind: TextBlockKind.Heading, Text: "Returns", Level: 2 });
		Assert.Contains(extracted.Blocks, b => b is { Kind: TextBlockKind.Paragraph, Text: "Within 30 days." });
		Assert.Contains(extracted.Blocks, b => b.Text == "Plan | Price");
	}

	[Fact]
	public void Markdown_HeadingsAndInlineSyntax()
	{
		const string Markdown = """
			# Help center

			## Returns

			You can **return** items within [30 days](https://example.com/returns).

			- Keep the receipt
			- Use the original box

			```
			# not a heading inside code
			```
			""";

		var extracted = new MarkdownTextExtractor().Extract(Encoding.UTF8.GetBytes(Markdown));

		Assert.Equal("Help center", extracted.Title);
		Assert.Contains(extracted.Blocks, b => b is { Kind: TextBlockKind.Heading, Text: "Returns", Level: 2 });
		Assert.Contains(extracted.Blocks, b => b.Text == "You can return items within 30 days.");
		Assert.DoesNotContain(extracted.Blocks, b => b.Text.Contains("not a heading", StringComparison.Ordinal) && b.Kind == TextBlockKind.Heading);
	}

	[Fact]
	public void Html_ReadsContentAndDropsChrome()
	{
		const string Html = """
			<html><head><title>FAQ</title><script>steal()</script></head>
			<body>
			  <nav>Home | Products | Login</nav>
			  <main>
			    <h1>Frequently asked</h1>
			    <h2>Returns</h2>
			    <p>Returns are accepted within <b>30 days</b>.</p>
			    <ul><li>Keep the receipt</li></ul>
			  </main>
			  <footer>© Acme</footer>
			</body></html>
			""";

		var extracted = new HtmlTextExtractor().Extract(Encoding.UTF8.GetBytes(Html));

		Assert.Equal("FAQ", extracted.Title);
		Assert.Contains(extracted.Blocks, b => b is { Kind: TextBlockKind.Heading, Text: "Returns", Level: 2 });
		Assert.Contains(extracted.Blocks, b => b.Text == "Returns are accepted within 30 days.");
		Assert.Contains(extracted.Blocks, b => b.Text == "Keep the receipt");
		var all = extracted.PlainText;
		Assert.DoesNotContain("Login", all, StringComparison.Ordinal);
		Assert.DoesNotContain("steal", all, StringComparison.Ordinal);
		Assert.DoesNotContain("Acme", all, StringComparison.Ordinal);
	}

	internal static byte[] BuildPdf(params string[] pages)
	{
		var builder = new PdfDocumentBuilder();
		var font = builder.AddStandard14Font(Standard14Font.Helvetica);
		const int FontSize = 12;
		const int Left = 50;
		const int Top = 750;
		foreach (var text in pages)
		{
			builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4)
				.AddText(text, FontSize, new UglyToad.PdfPig.Core.PdfPoint(Left, Top), font);
		}

		return builder.Build();
	}

	private static byte[] BuildDocx()
	{
		using var stream = new MemoryStream();
		using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
		{
			var main = document.AddMainDocumentPart();
			main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(new Body(
				Styled("Store policies", "Title"),
				Styled("Returns", "Heading2"),
				new Paragraph(new Run(new Text("Within 30 days."))),
				new Table(new TableRow(Cell("Plan"), Cell("Price")))));
		}

		return stream.ToArray();

		static Paragraph Styled(string text, string style) =>
			new(new ParagraphProperties(new ParagraphStyleId { Val = style }), new Run(new Text(text)));

		static TableCell Cell(string text) => new(new Paragraph(new Run(new Text(text))));
	}
}
