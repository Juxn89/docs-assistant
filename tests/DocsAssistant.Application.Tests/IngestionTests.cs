using System.Text;
using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Common;
using DocsAssistant.Application.Features.Ingest;
using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Documents;
using DocsAssistant.Domain.Tenants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocsAssistant.Application.Tests;

public sealed class IngestionTests
{
	private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

	private readonly TenantContext _context = new();
	private readonly FakeTenantRepository _tenants = new();
	private readonly FakeDocumentRepository _documents;
	private readonly FakeQueue _queue = new();
	private readonly FakeEmbedder _embedder = new();
	private readonly IngestionOptions _options = new() { MaxFileBytes = 1024, MaxAttempts = 2, EmbeddingBatchSize = 2 };
	private readonly Tenant _tenant;

	public IngestionTests()
	{
		_documents = new FakeDocumentRepository(_context);
		_tenant = Tenant.Create("Acme", "pk_acme", "help@acme.test", ContentLanguage.English, [], Now);
		_tenants.Add(_tenant);
	}

	private RegisterDocument Register(ITenantContext? context = null) =>
		new(context ?? _context, _tenants, _documents, _queue, Options.Create(_options), new FixedClock(Now));

	private ProcessDocument Process(params ITextExtractor[] extractors) =>
		new(_context, _documents, extractors, _embedder, Options.Create(_options), new FixedClock(Now), NullLogger<ProcessDocument>.Instance);

	private static ReadOnlyMemory<byte> Utf8(string text) => Encoding.UTF8.GetBytes(text);

	[Fact]
	public async Task Upload_RegistersStoresAndEnqueues()
	{
		var result = await new UploadDocument(Register()).HandleAsync(
			new UploadDocumentRequest(_tenant.Id.Value, "faq.md", "text/markdown", Utf8("# FAQ\nHello"), null),
			CancellationToken.None);

		Assert.True(result.IsSuccess);
		Assert.False(result.Value.Duplicate);
		Assert.Equal(DocumentSourceType.Markdown, result.Value.Document.Format);
		Assert.Equal("faq", result.Value.Document.Title);
		Assert.Equal(DocumentStatus.Pending, result.Value.Document.Status);
		Assert.Single(_queue.Jobs);
	}

	[Fact]
	public async Task Upload_SameBytesTwice_IsADuplicateAndEnqueuesOnce()
	{
		var upload = new UploadDocument(Register());
		var request = new UploadDocumentRequest(_tenant.Id.Value, "a.txt", "text/plain", Utf8("same bytes"), null);

		var first = await upload.HandleAsync(request, CancellationToken.None);
		var second = await upload.HandleAsync(request with { FileName = "renamed.txt" }, CancellationToken.None);

		Assert.True(second.Value.Duplicate);
		Assert.Equal(first.Value.Document.Id, second.Value.Document.Id);
		Assert.Single(_documents.Stored);
		Assert.Single(_queue.Jobs);
	}

	[Fact]
	public async Task Upload_TooLarge_IsAValidationError()
	{
		var result = await new UploadDocument(Register()).HandleAsync(
			new UploadDocumentRequest(_tenant.Id.Value, "big.txt", "text/plain", new byte[_options.MaxFileBytes + 1], null),
			CancellationToken.None);

		Assert.Equal("document.too_large", result.Error?.Code);
		Assert.Empty(_documents.Stored);
	}

	[Fact]
	public async Task Upload_UnknownTenant_IsNotFound()
	{
		var result = await new UploadDocument(Register()).HandleAsync(
			new UploadDocumentRequest(Guid.NewGuid(), "a.txt", null, Utf8("x"), null),
			CancellationToken.None);

		Assert.Equal(ErrorKind.NotFound, result.Error?.Kind);
	}

	[Fact]
	public async Task AddUrl_FetchFailure_IsReturnedAsIs()
	{
		var blocked = Error.Validation("url.blocked", "Private address");
		var addUrl = new AddUrlDocument(new FakeUrlFetcher(blocked), Register());

		var result = await addUrl.HandleAsync(new AddUrlDocumentRequest(_tenant.Id.Value, "http://10.0.0.1/", null), CancellationToken.None);

		Assert.Equal("url.blocked", result.Error?.Code);
	}

	[Theory]
	[InlineData("not a url")]
	[InlineData("ftp://example.com/file")]
	[InlineData("/relative/path")]
	public async Task AddUrl_InvalidUrl_IsRejectedBeforeFetching(string url)
	{
		var addUrl = new AddUrlDocument(new FakeUrlFetcher(Error.Upstream("never", "should not be called")), Register());

		var result = await addUrl.HandleAsync(new AddUrlDocumentRequest(_tenant.Id.Value, url, null), CancellationToken.None);

		Assert.Equal("url.invalid", result.Error?.Code);
	}

	[Fact]
	public async Task AddUrl_UsesTheFinalUrlAsSource()
	{
		var fetched = new FetchedResource(new Uri("https://example.com/final"), "text/html", Utf8("<html><p>Hi</p></html>"), null);
		var addUrl = new AddUrlDocument(new FakeUrlFetcher(fetched), Register());

		var result = await addUrl.HandleAsync(new AddUrlDocumentRequest(_tenant.Id.Value, "https://example.com/start", null), CancellationToken.None);

		Assert.Equal("https://example.com/final", result.Value.Document.SourceUri);
		Assert.Equal(DocumentSourceType.Html, result.Value.Document.Format);
	}

	[Fact]
	public async Task Process_HappyPath_ChunksEmbedsInBatchesAndDetectsLanguage()
	{
		var job = await RegisterTextAsync("Las devoluciones se procesan en tres días y el reembolso llega a la cuenta del cliente.");
		var paragraphs = Enumerable.Range(1, 5)
			.Select(i => TextBlock.Paragraph(new string('x', 300) + $" párrafo {i} de la política de la tienda."))
			.ToList();
		var extractor = new FakeExtractor(DocumentSourceType.PlainText, _ => new ExtractedText(null, paragraphs));

		var outcome = await Process(extractor).HandleAsync(job, CancellationToken.None);

		Assert.Equal(ProcessingOutcome.Ready, outcome);
		var document = Assert.Single(_documents.Stored);
		Assert.Equal(DocumentStatus.Ready, document.Status);
		Assert.Equal(ContentLanguage.Spanish, document.Language);
		Assert.Equal(document.ChunkCount, _documents.Chunks[document.Id].Count);
		Assert.All(_embedder.BatchSizes, size => Assert.True(size <= _options.EmbeddingBatchSize));
	}

	[Fact]
	public async Task Process_NoText_IsAPermanentFailure()
	{
		var job = await RegisterTextAsync("placeholder");
		var extractor = new FakeExtractor(DocumentSourceType.PlainText, _ => new ExtractedText(null, []));

		var outcome = await Process(extractor).HandleAsync(job, CancellationToken.None);

		Assert.Equal(ProcessingOutcome.Failed, outcome);
		Assert.Equal(DocumentStatus.Failed, _documents.Stored[0].Status);
	}

	[Fact]
	public async Task Process_EmbedderFails_RetriesThenFails_AndDiscardsTheAttempt()
	{
		var job = await RegisterTextAsync("some text that will never be embedded");
		_embedder.FailWith = new HttpRequestException("model unavailable");
		var process = Process(new FakeExtractor(DocumentSourceType.PlainText, c => new ExtractedText(null, [TextBlock.Paragraph(Encoding.UTF8.GetString(c.Span))])));

		var first = await process.HandleAsync(job, CancellationToken.None);
		var second = await process.HandleAsync(job, CancellationToken.None);

		Assert.Equal(ProcessingOutcome.WillRetry, first);
		Assert.Equal(ProcessingOutcome.Failed, second);
		var document = _documents.Stored[0];
		Assert.Equal(DocumentStatus.Failed, document.Status);
		Assert.Contains("model unavailable", document.Error, StringComparison.Ordinal);
		Assert.Equal(2, _documents.DiscardCount);
		Assert.False(_documents.Chunks.ContainsKey(document.Id));
	}

	[Fact]
	public async Task Process_FinalWriteFails_DoesNotLeaveTheDocumentReady()
	{
		var job = await RegisterTextAsync("text");
		_documents.FailIngestionWith = new InvalidOperationException("connection reset");
		var process = Process(new FakeExtractor(DocumentSourceType.PlainText, _ => new ExtractedText(null, [TextBlock.Paragraph("text")])));

		var outcome = await process.HandleAsync(job, CancellationToken.None);

		Assert.Equal(ProcessingOutcome.WillRetry, outcome);
		Assert.Equal(1, _documents.DiscardCount);
		var document = _documents.Stored[0];
		Assert.Equal(DocumentStatus.Pending, document.Status);
		Assert.Equal(0, document.ChunkCount);
		Assert.Null(document.ProcessedAt);
	}

	[Fact]
	public async Task Process_AlreadyReady_IsSkipped()
	{
		var job = await RegisterTextAsync("text");
		var process = Process(new FakeExtractor(DocumentSourceType.PlainText, _ => new ExtractedText(null, [TextBlock.Paragraph("text")])));
		await process.HandleAsync(job, CancellationToken.None);

		var again = await Process(new FakeExtractor(DocumentSourceType.PlainText, _ => throw new InvalidOperationException("must not run")))
			.HandleAsync(job, CancellationToken.None);

		Assert.Equal(ProcessingOutcome.Skipped, again);
	}

	[Fact]
	public async Task Process_JobForAnotherTenantsDocument_IsSkipped()
	{
		var job = await RegisterTextAsync("text");
		var otherContext = new TenantContext();
		var forged = job with { TenantId = TenantId.New() };
		var process = new ProcessDocument(
			otherContext, new FakeDocumentRepository(otherContext), [], _embedder, Options.Create(_options), new FixedClock(Now), NullLogger<ProcessDocument>.Instance);

		Assert.Equal(ProcessingOutcome.Skipped, await process.HandleAsync(forged, CancellationToken.None));
	}

	private async Task<IngestionJob> RegisterTextAsync(string text)
	{
		var result = await new UploadDocument(Register()).HandleAsync(
			new UploadDocumentRequest(_tenant.Id.Value, "doc.txt", "text/plain", Utf8(text), null),
			CancellationToken.None);
		Assert.True(result.IsSuccess, result.Error?.Message);
		return _queue.Jobs[^1];
	}
}
