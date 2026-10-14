using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Features.Chat;
using DocsAssistant.Domain.Common;
using Microsoft.Extensions.Options;

namespace DocsAssistant.Integration.Tests;

/// <summary>Sentence selection needs real semantics, so it is tested with the real embedding model.</summary>
[Collection(EmbedderCollection.Name)]
public sealed class ExtractiveAnswererTests(EmbedderFixture model)
{
	private static readonly RetrievedChunk[] Passages =
	[
		Passage("Shipping is free on orders over 50 dollars. Orders arrive in three to five business days. We ship to every province."),
		Passage("Returns are accepted within 30 days of delivery. The item must be unused and in its original box. Refunds go back to the original card."),
		Passage("Our support team answers chats from Monday to Friday. On weekends, write to the help address and we reply on Monday."),
	];

	private ExtractiveAnswerer Answerer(int maxSentences = 3) =>
		new(model.Embedder, Options.Create(new ExtractiveOptions { MaxSentences = maxSentences }));

	[Theory]
	[InlineData("How many days do I have to return a product?")]
	[InlineData("¿Cuántos días tengo para devolver un producto?")]
	public async Task PicksTheSentenceThatAnswers_InEitherLanguage(string question)
	{
		var sentences = await Answerer().SelectAsync(question, Passages, CancellationToken.None);

		Assert.Contains(sentences, s => s.Text == "Returns are accepted within 30 days of delivery." && s.Citation == 2);
	}

	[Fact]
	public async Task KeepsReadingOrder_AndRespectsTheLimit()
	{
		var sentences = await Answerer(maxSentences: 2).SelectAsync("What does shipping cost and how long does it take?", Passages, CancellationToken.None);

		Assert.InRange(sentences.Count, 1, 2);
		Assert.Equal(sentences.OrderBy(s => s.Citation).ThenBy(s => s.Position), sentences);
		Assert.All(sentences, s => Assert.Equal(1, s.Citation));
	}

	private static RetrievedChunk Passage(string text) =>
		new(ChunkId.New(), DocumentId.New(), "Help center", "help.md", 0, null, text, 0.9);
}
