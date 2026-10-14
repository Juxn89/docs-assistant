using DocsAssistant.Application.Abstractions;
using DocsAssistant.Domain.Documents;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocsAssistant.Infrastructure.Extraction;

/// <summary>
/// Word headings are paragraphs whose style is Heading1…Heading9 (or "Title"); everything else is a
/// paragraph. Tables are read row by row so their text is not lost.
/// </summary>
internal sealed class DocxTextExtractor : ITextExtractor
{
	private const string HeadingStylePrefix = "Heading";
	private const string TitleStyle = "Title";

	public DocumentSourceType Format => DocumentSourceType.Docx;

	public ExtractedText Extract(ReadOnlyMemory<byte> content)
	{
		using var stream = new MemoryStream(content.ToArray(), writable: false);
		using var document = WordprocessingDocument.Open(stream, isEditable: false);
		var body = document.MainDocumentPart?.Document?.Body;
		if (body is null)
		{
			return new ExtractedText(null, []);
		}

		var blocks = new List<TextBlock>();
		string? title = document.PackageProperties.Title;

		foreach (var element in body.ChildElements)
		{
			switch (element)
			{
				case Paragraph paragraph:
					AddParagraph(paragraph, blocks, ref title);
					break;
				case Table table:
					foreach (var row in table.Elements<TableRow>())
					{
						var cells = row.Elements<TableCell>().Select(c => c.InnerText.Trim()).Where(t => t.Length > 0);
						var line = string.Join(" | ", cells);
						if (line.Length > 0)
						{
							blocks.Add(TextBlock.Paragraph(line));
						}
					}

					break;
			}
		}

		return new ExtractedText(string.IsNullOrWhiteSpace(title) ? null : title, blocks);
	}

	private static void AddParagraph(Paragraph paragraph, List<TextBlock> blocks, ref string? title)
	{
		var text = paragraph.InnerText.Trim();
		if (text.Length == 0)
		{
			return;
		}

		var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? string.Empty;
		if (style.Equals(TitleStyle, StringComparison.OrdinalIgnoreCase))
		{
			title ??= text;
			blocks.Add(TextBlock.Heading(text, 1));
		}
		else if (style.StartsWith(HeadingStylePrefix, StringComparison.OrdinalIgnoreCase)
			&& int.TryParse(style.AsSpan(HeadingStylePrefix.Length), out var level))
		{
			blocks.Add(TextBlock.Heading(text, level));
		}
		else
		{
			blocks.Add(TextBlock.Paragraph(text));
		}
	}
}
