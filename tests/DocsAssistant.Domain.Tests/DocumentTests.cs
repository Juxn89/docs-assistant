using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Documents;

namespace DocsAssistant.Domain.Tests;

public sealed class DocumentTests
{
	private const int MaxAttempts = 3;
	private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

	private static Document NewDocument() =>
		Document.Register(
			TenantId.New(),
			"CV",
			DocumentSourceType.Pdf,
			"cv.pdf",
			ContentHash.Compute([1, 2, 3]),
			ContentLanguage.English,
			Now);

	[Fact]
	public void Register_StartsPending()
	{
		var document = NewDocument();

		Assert.Equal(DocumentStatus.Pending, document.Status);
		Assert.Equal(0, document.Attempts);
	}

	[Fact]
	public void HappyPath_PendingProcessingReady()
	{
		var document = NewDocument();

		document.StartProcessing();
		document.MarkReady(chunkCount: 4, Now);

		Assert.Equal(DocumentStatus.Ready, document.Status);
		Assert.Equal(4, document.ChunkCount);
		Assert.Equal(Now, document.ProcessedAt);
	}

	[Fact]
	public void RecordFailure_BeforeTheLimit_GoesBackToPending()
	{
		var document = NewDocument();
		document.StartProcessing();

		var willRetry = document.RecordFailure("timeout", MaxAttempts, Now);

		Assert.True(willRetry);
		Assert.Equal(DocumentStatus.Pending, document.Status);
		Assert.Equal("timeout", document.Error);
	}

	[Fact]
	public void RecordFailure_AtTheLimit_IsPermanent()
	{
		var document = NewDocument();
		for (var attempt = 1; attempt < MaxAttempts; attempt++)
		{
			document.StartProcessing();
			document.RecordFailure("timeout", MaxAttempts, Now);
		}

		document.StartProcessing();
		var willRetry = document.RecordFailure("timeout", MaxAttempts, Now);

		Assert.False(willRetry);
		Assert.Equal(DocumentStatus.Failed, document.Status);
		Assert.Equal(MaxAttempts, document.Attempts);
	}

	[Fact]
	public void StartProcessing_ClearsThePreviousError()
	{
		var document = NewDocument();
		document.StartProcessing();
		document.RecordFailure("timeout", MaxAttempts, Now);

		document.StartProcessing();

		Assert.Null(document.Error);
	}

	[Fact]
	public void MarkReady_WithoutProcessing_Throws() =>
		Assert.Throws<DomainException>(() => NewDocument().MarkReady(1, Now));

	[Fact]
	public void StartProcessing_Twice_Throws()
	{
		var document = NewDocument();
		document.StartProcessing();

		Assert.Throws<DomainException>(document.StartProcessing);
	}

	[Fact]
	public void MarkEmpty_IsAPermanentFailure()
	{
		var document = NewDocument();
		document.StartProcessing();

		document.MarkEmpty(Now);

		Assert.Equal(DocumentStatus.Failed, document.Status);
		Assert.NotNull(document.Error);
	}

	[Fact]
	public void ContentHash_IsStableAndHex()
	{
		var first = ContentHash.Compute("hello"u8);
		var second = ContentHash.Compute("hello"u8);

		Assert.Equal(first, second);
		Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", first.Value);
		Assert.Equal(first, ContentHash.FromHex(first.Value.ToUpperInvariant()));
	}

	[Theory]
	[InlineData("")]
	[InlineData("abc")]
	[InlineData("zz24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")]
	public void ContentHash_FromHex_RejectsGarbage(string hex) =>
		Assert.ThrowsAny<ArgumentException>(() => ContentHash.FromHex(hex));
}
