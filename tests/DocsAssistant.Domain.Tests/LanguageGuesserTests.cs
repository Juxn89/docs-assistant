using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Documents;

namespace DocsAssistant.Domain.Tests;

public sealed class LanguageGuesserTests
{
	[Fact]
	public void Spanish_IsDetected() =>
		Assert.Equal(
			ContentLanguage.Spanish,
			LanguageGuesser.Guess(
				"Las devoluciones se procesan en tres días hábiles y el reembolso llega a la cuenta del cliente.",
				ContentLanguage.English));

	[Fact]
	public void English_IsDetected() =>
		Assert.Equal(
			ContentLanguage.English,
			LanguageGuesser.Guess(
				"Returns are processed within three business days and the refund is sent to the card that was used.",
				ContentLanguage.Spanish));

	[Theory]
	[InlineData("")]
	[InlineData("C# .NET 10 EF Core pgvector HNSW")]
	[InlineData("el the")]
	public void ThinEvidence_KeepsTheFallback(string text) =>
		Assert.Equal(ContentLanguage.Spanish, LanguageGuesser.Guess(text, ContentLanguage.Spanish));

	[Fact]
	public void EvenlyMixed_KeepsTheFallback() =>
		Assert.Equal(
			ContentLanguage.English,
			LanguageGuesser.Guess("the de and la of el to que in los is las", ContentLanguage.English));
}
