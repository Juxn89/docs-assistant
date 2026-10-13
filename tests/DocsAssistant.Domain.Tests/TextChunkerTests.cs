using DocsAssistant.Domain.Documents;

namespace DocsAssistant.Domain.Tests;

public sealed class TextChunkerTests
{
	private static readonly ChunkingOptions Small = new(maxTokens: 20, overlapTokens: 6);

	[Fact]
	public void Chunk_EmptyInput_ReturnsNoChunks()
	{
		var chunks = TextChunker.Chunk([], ChunkingOptions.Default);

		Assert.Empty(chunks);
	}

	[Fact]
	public void Chunk_ShortParagraphs_PacksThemIntoOneChunk()
	{
		var chunks = TextChunker.Chunk(
			[TextBlock.Paragraph("First paragraph."), TextBlock.Paragraph("Second paragraph.")],
			ChunkingOptions.Default);

		var chunk = Assert.Single(chunks);
		Assert.Equal("First paragraph. Second paragraph.", chunk.Text);
		Assert.Null(chunk.Heading);
	}

	[Fact]
	public void Chunk_Headings_AreHardBoundariesAndBuildAPath()
	{
		var chunks = TextChunker.Chunk(
			[
				TextBlock.Heading("Experience", 1),
				TextBlock.Paragraph("Intro."),
				TextBlock.Heading("Stepstone", 2),
				TextBlock.Paragraph("Microfrontends."),
				TextBlock.Heading("Education", 1),
				TextBlock.Paragraph("UNI."),
			],
			ChunkingOptions.Default);

		Assert.Collection(
			chunks,
			c => Assert.Equal(("Experience", "Intro."), (c.Heading, c.Text)),
			c => Assert.Equal(("Experience › Stepstone", "Microfrontends."), (c.Heading, c.Text)),
			c => Assert.Equal(("Education", "UNI."), (c.Heading, c.Text)));
	}

	[Fact]
	public void Chunk_NoChunkExceedsTheCeiling()
	{
		var longParagraph = string.Join(' ', Enumerable.Range(1, 60).Select(i => $"Sentence number {i}."));

		var chunks = TextChunker.Chunk([TextBlock.Paragraph(longParagraph)], Small);

		Assert.True(chunks.Count > 1);
		Assert.All(chunks, c => Assert.True(c.EstimatedTokens <= Small.MaxTokens, $"{c.EstimatedTokens} tokens"));
	}

	[Fact]
	public void Chunk_ConsecutiveChunksInASection_ShareOverlap()
	{
		var paragraph = string.Join(' ', Enumerable.Range(1, 12).Select(i => $"Fact {i} here."));

		var chunks = TextChunker.Chunk([TextBlock.Paragraph(paragraph)], Small);

		for (var i = 1; i < chunks.Count; i++)
		{
			var lastSentenceOfPrevious = chunks[i - 1].Text.Split(". ").Last().TrimEnd('.');
			Assert.Contains(lastSentenceOfPrevious, chunks[i].Text, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void Chunk_SentenceLongerThanTheCeiling_IsSplitByWords()
	{
		var monster = string.Join(' ', Enumerable.Repeat("word", 200));

		var chunks = TextChunker.Chunk([TextBlock.Paragraph(monster)], Small);

		Assert.All(chunks, c => Assert.True(c.EstimatedTokens <= Small.MaxTokens));
		Assert.Equal(200, chunks.Sum(c => c.Text.Split(' ').Length));
	}

	[Fact]
	public void Chunk_OrdinalsAreSequential()
	{
		var chunks = TextChunker.Chunk(
			[
				TextBlock.Heading("A", 1),
				TextBlock.Paragraph(string.Join(' ', Enumerable.Repeat("Alpha beta gamma.", 20))),
				TextBlock.Heading("B", 1),
				TextBlock.Paragraph("Delta."),
			],
			Small);

		Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Ordinal));
	}

	[Fact]
	public void Chunk_CollapsesWhitespaceAndSkipsBlankBlocks()
	{
		var chunks = TextChunker.Chunk(
			[TextBlock.Paragraph("  Hello \n\n  world  "), TextBlock.Paragraph("   "), TextBlock.Heading(" ", 1)],
			ChunkingOptions.Default);

		Assert.Equal("Hello world", Assert.Single(chunks).Text);
	}

	[Theory]
	[InlineData(0, 0)]
	[InlineData(10, 10)]
	[InlineData(10, 11)]
	[InlineData(10, -1)]
	public void ChunkingOptions_RejectsInvalidSizes(int max, int overlap) =>
		Assert.ThrowsAny<ArgumentOutOfRangeException>(() => new ChunkingOptions(max, overlap));
}
