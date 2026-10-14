using System.Text;
using System.Text.RegularExpressions;
using DocsAssistant.Application.Abstractions;
using DocsAssistant.Domain.Documents;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace DocsAssistant.Infrastructure.Extraction;

/// <summary>
/// PDFs carry no reliable heading structure, so each page becomes a section titled with its number.
/// That is also what makes citations useful: "Política de devoluciones, p. 3" is somewhere a person can
/// go and check.
/// </summary>
internal sealed partial class PdfTextExtractor : ITextExtractor
{
	public const string PageHeadingFormat = "p. {0}";

	public DocumentSourceType Format => DocumentSourceType.Pdf;

	public ExtractedText Extract(ReadOnlyMemory<byte> content)
	{
		using var pdf = PdfDocument.Open(content.ToArray());
		var blocks = new List<TextBlock>();
		foreach (var page in pdf.GetPages())
		{
			var text = ContentOrderTextExtractor.GetText(page);
			var paragraphs = BlankLines().Split(text).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
			if (paragraphs.Count == 0)
			{
				continue;
			}

			blocks.Add(TextBlock.Heading(string.Format(System.Globalization.CultureInfo.InvariantCulture, PageHeadingFormat, page.Number), 1));
			blocks.AddRange(paragraphs.Select(TextBlock.Paragraph));
		}

		var title = pdf.Information.Title;
		return new ExtractedText(string.IsNullOrWhiteSpace(title) ? null : title, blocks);
	}

	[GeneratedRegex(@"\n\s*\n")]
	private static partial Regex BlankLines();
}

internal sealed class PlainTextExtractor : ITextExtractor
{
	public DocumentSourceType Format => DocumentSourceType.PlainText;

	public ExtractedText Extract(ReadOnlyMemory<byte> content) =>
		new(null, [.. Paragraphs.Split(Encoding.UTF8.GetString(content.Span)).Select(TextBlock.Paragraph)]);
}

/// <summary>
/// Markdown headings (# … ######) become structure; inline syntax is stripped so neither the
/// embedding nor the full-text index sees asterisks and link brackets.
/// </summary>
internal sealed partial class MarkdownTextExtractor : ITextExtractor
{
	public DocumentSourceType Format => DocumentSourceType.Markdown;

	public ExtractedText Extract(ReadOnlyMemory<byte> content)
	{
		var blocks = new List<TextBlock>();
		var paragraph = new StringBuilder();
		string? title = null;
		var inCodeFence = false;

		void FlushParagraph()
		{
			if (paragraph.Length > 0)
			{
				blocks.Add(TextBlock.Paragraph(StripInline(paragraph.ToString())));
				paragraph.Clear();
			}
		}

		foreach (var rawLine in Encoding.UTF8.GetString(content.Span).Split('\n'))
		{
			var line = rawLine.TrimEnd('\r');
			if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
			{
				inCodeFence = !inCodeFence;
				continue;
			}

			var heading = inCodeFence ? null : Heading().Match(line);
			if (heading is { Success: true })
			{
				FlushParagraph();
				var text = StripInline(heading.Groups["text"].Value);
				var level = heading.Groups["hashes"].Value.Length;
				title ??= level == 1 ? text : null;
				blocks.Add(TextBlock.Heading(text, level));
			}
			else if (string.IsNullOrWhiteSpace(line))
			{
				FlushParagraph();
			}
			else
			{
				paragraph.Append(ListMarker().Replace(line, string.Empty)).Append(' ');
			}
		}

		FlushParagraph();
		return new ExtractedText(title, blocks);
	}

	private static string StripInline(string text)
	{
		text = Image().Replace(text, "$1");
		text = Link().Replace(text, "$1");
		return Emphasis().Replace(text, string.Empty).Trim();
	}

	[GeneratedRegex(@"^(?<hashes>#{1,6})\s+(?<text>.+?)\s*#*\s*$")]
	private static partial Regex Heading();

	[GeneratedRegex(@"^\s*([-*+]|\d+\.)\s+")]
	private static partial Regex ListMarker();

	[GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
	private static partial Regex Image();

	[GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
	private static partial Regex Link();

	[GeneratedRegex(@"[*_`~]{1,3}")]
	private static partial Regex Emphasis();
}

internal static partial class Paragraphs
{
	public static IEnumerable<string> Split(string text) =>
		BlankLines().Split(text.Replace("\r\n", "\n", StringComparison.Ordinal)).Where(p => !string.IsNullOrWhiteSpace(p));

	[GeneratedRegex(@"\n\s*\n")]
	private static partial Regex BlankLines();
}
