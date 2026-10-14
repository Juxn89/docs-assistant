using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using DocsAssistant.Application.Abstractions;
using DocsAssistant.Domain.Documents;

namespace DocsAssistant.Infrastructure.Extraction;

/// <summary>
/// Reads the content of a web page, not its chrome: navigation, headers, footers, scripts and forms
/// are dropped, and when the page marks its main content (main, article) only that is read.
/// The HTML is parsed, never rendered: no script runs and no subresource is fetched.
/// </summary>
internal sealed class HtmlTextExtractor : ITextExtractor
{
	private static readonly string[] Noise =
		["script", "style", "noscript", "template", "svg", "nav", "header", "footer", "aside", "form", "iframe", "button"];

	private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
	{
		"P", "LI", "BLOCKQUOTE", "PRE", "TD", "TH", "DT", "DD", "FIGCAPTION",
	};

	public DocumentSourceType Format => DocumentSourceType.Html;

	public ExtractedText Extract(ReadOnlyMemory<byte> content)
	{
		var parser = new HtmlParser();
		using var document = parser.ParseDocument(Encoding.UTF8.GetString(content.Span));

		foreach (var element in document.QuerySelectorAll(string.Join(',', Noise)).ToList())
		{
			element.Remove();
		}

		var root = (IElement?)document.QuerySelector("main") ?? document.QuerySelector("article") ?? document.Body;
		var blocks = new List<TextBlock>();
		if (root is not null)
		{
			Walk(root, blocks);
		}

		var title = document.Title?.Trim();
		return new ExtractedText(string.IsNullOrEmpty(title) ? null : title, blocks);
	}

	private static void Walk(IElement element, List<TextBlock> blocks)
	{
		foreach (var child in element.Children)
		{
			var tag = child.TagName;
			if (tag.Length == 2 && tag[0] == 'H' && char.IsAsciiDigit(tag[1]))
			{
				AddIfText(blocks, child.TextContent, level => TextBlock.Heading(level, tag[1] - '0'));
			}
			else if (BlockTags.Contains(tag))
			{
				AddIfText(blocks, child.TextContent, TextBlock.Paragraph);
			}
			else
			{
				// Containers (div, section, ul…): descend. Loose text directly inside them is kept too.
				var ownText = string.Concat(child.ChildNodes.OfType<IText>().Select(t => t.Data));
				AddIfText(blocks, ownText, TextBlock.Paragraph);
				Walk(child, blocks);
			}
		}
	}

	private static void AddIfText(List<TextBlock> blocks, string text, Func<string, TextBlock> create)
	{
		if (!string.IsNullOrWhiteSpace(text))
		{
			blocks.Add(create(text.Trim()));
		}
	}
}
