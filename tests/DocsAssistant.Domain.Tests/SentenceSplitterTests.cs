using DocsAssistant.Domain.Documents;

namespace DocsAssistant.Domain.Tests;

public sealed class SentenceSplitterTests
{
	[Fact]
	public void Splits_OnTerminatorsFollowedByACapital()
	{
		var sentences = SentenceSplitter.Split("Returns are accepted within 30 days. Refunds take five business days to arrive.");

		Assert.Equal(["Returns are accepted within 30 days.", "Refunds take five business days to arrive."], sentences);
	}

	[Fact]
	public void Splits_SpanishOpeningMarks()
	{
		var sentences = SentenceSplitter.Split("Los envíos tardan tres días hábiles. ¿Necesitas factura? Pídela al momento de comprar el producto.");

		Assert.Equal(["Los envíos tardan tres días hábiles.", "Pídela al momento de comprar el producto."], sentences);
	}

	[Theory]
	[InlineData("Use e.g. the default exchange for a single queue job.")]
	[InlineData("Upgrade to v1.2.3 before enabling the feature flag.")]
	[InlineData("Delivery takes 3.5 days on average in the capital.")]
	public void DoesNotSplit_AbbreviationsVersionsOrDecimals(string text) =>
		Assert.Equal([text], SentenceSplitter.Split(text));

	[Fact]
	public void DropsFragmentsTooShortToCarryAnAnswer() =>
		Assert.Equal(["The policy changed in March of this year."], SentenceSplitter.Split("Yes. The policy changed in March of this year. See below."));
}
