using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Common;
using DocsAssistant.Application.Features.Chat;
using DocsAssistant.Application.Features.Handoff;
using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Conversations;
using DocsAssistant.Domain.Handoff;
using DocsAssistant.Domain.Tenants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocsAssistant.Application.Tests;

public sealed class HandoffTests
{
	private static readonly DateTimeOffset Start = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

	private readonly ManualClock _clock = new(Start);
	private readonly FakeTenantRepository _tenants = new();
	private readonly InMemoryHandoffStore _store = new();
	private readonly LeadQueueSpy _queue = new();
	private readonly NotifierSpy _notifier = new();
	private readonly HandoffOptions _options = new();
	private readonly Tenant _tenant;

	public HandoffTests()
	{
		_tenant = Tenant.Create("Acme", "pk_acme", "help@acme.test", ContentLanguage.Spanish, [], Start);
		_tenants.Add(_tenant);
	}

	private Task<Result<HandoffAccepted>> HandoffAsync(HandoffRequest request, Tenant? tenant = null)
	{
		var context = new TenantContext();
		var useCase = new RequestHandoff(
			context, new LeadStore(_store, context), new ConversationStore(_store, context), _queue, Options.Create(_options), _clock, NullLogger<RequestHandoff>.Instance);
		var target = tenant ?? _tenant;
		return useCase.HandleAsync(target.Id, target.DefaultLanguage, request, CancellationToken.None);
	}

	private Task<NotifyOutcome> NotifyAsync(LeadJob job)
	{
		var context = new TenantContext();
		var useCase = new NotifyLead(
			context, _tenants, new LeadStore(_store, context), new ConversationStore(_store, context), _notifier, Options.Create(_options), _clock, NullLogger<NotifyLead>.Instance);
		return useCase.HandleAsync(job, CancellationToken.None);
	}

	private Conversation SeedConversation(Tenant tenant)
	{
		var conversation = Conversation.Start(tenant.Id, Start);
		_store.Conversations.Add(conversation);
		_store.Messages.Add(conversation.AddVisitorMessage("¿Hacen envíos a Guatemala?", Start));
		_store.Messages.Add(conversation.AddAssistantMessage(
			AnswerTexts.NoInformation(spanish: true), nameof(AnswerOutcome.AbstainedNoContext), [], Start.AddSeconds(1)));
		return conversation;
	}

	[Theory]
	[InlineData("", "Hola", "handoff.invalid_email")]
	[InlineData("not-an-email", "Hola", "handoff.invalid_email")]
	[InlineData("Ana <ana@example.com>", "Hola", "handoff.invalid_email")]
	[InlineData("ana@example.com\r\nBcc: victim@example.com", "Hola", "handoff.invalid_email")]
	[InlineData("ana@example.com", "   ", "handoff.message_required")]
	public async Task InvalidRequest_IsRejected_AndNothingIsStored(string email, string message, string code)
	{
		var result = await HandoffAsync(new HandoffRequest(null, email, null, message));

		Assert.False(result.IsSuccess);
		Assert.Equal(code, result.Error!.Code);
		Assert.Empty(_store.Leads);
		Assert.Empty(_queue.Jobs);
	}

	[Fact]
	public async Task OverlongMessageOrMultilineName_IsRejected()
	{
		var tooLong = await HandoffAsync(new HandoffRequest(null, "ana@example.com", null, new string('x', Lead.MessageMaxLength + 1)));
		var badName = await HandoffAsync(new HandoffRequest(null, "ana@example.com", "Ana\nBcc: x@y.z", "Hola"));

		Assert.Equal("handoff.message_too_long", tooLong.Error!.Code);
		Assert.Equal("handoff.invalid_name", badName.Error!.Code);
	}

	[Fact]
	public async Task ValidRequest_StoresTheLeadWithItsConversation_AndQueuesIt()
	{
		var conversation = SeedConversation(_tenant);

		var result = await HandoffAsync(new HandoffRequest(conversation.Id.Value, " ana@example.com ", " Ana ", " Necesito una cotización. "));

		var lead = Assert.Single(_store.Leads);
		Assert.Equal(lead.Id.Value, result.Value.LeadId);
		Assert.Equal(conversation.Id, lead.ConversationId);
		Assert.Equal("ana@example.com", lead.VisitorEmail);
		Assert.Equal("Ana", lead.VisitorName);
		Assert.Equal("Necesito una cotización.", lead.Message);
		Assert.Equal(new LeadJob(_tenant.Id, lead.Id), Assert.Single(_queue.Jobs));
	}

	[Fact]
	public async Task AnotherTenantsConversation_IsNotAttached()
	{
		var other = Tenant.Create("Other", "pk_other", "ops@other.test", ContentLanguage.English, [], Start);
		var foreign = SeedConversation(other);

		await HandoffAsync(new HandoffRequest(foreign.Id.Value, "ana@example.com", null, "Hola"));

		Assert.Null(Assert.Single(_store.Leads).ConversationId);
	}

	[Fact]
	public async Task DoubleSubmit_ReturnsTheSameLead_UntilTheWindowPasses()
	{
		var request = new HandoffRequest(null, "ana@example.com", null, "Llámenme");

		var first = await HandoffAsync(request);
		_clock.Advance(_options.DuplicateWindow - TimeSpan.FromSeconds(1));
		var second = await HandoffAsync(request);
		_clock.Advance(TimeSpan.FromSeconds(2));
		var third = await HandoffAsync(request);

		Assert.Equal(first.Value.LeadId, second.Value.LeadId);
		Assert.NotEqual(first.Value.LeadId, third.Value.LeadId);
		Assert.Equal(2, _store.Leads.Count);
		Assert.Equal(2, _queue.Jobs.Count);
	}

	[Fact]
	public async Task Notify_SendsTheTranscriptToTheTenant_WithTheVisitorAsReplyTo()
	{
		var conversation = SeedConversation(_tenant);
		await HandoffAsync(new HandoffRequest(conversation.Id.Value, "ana@example.com", "Ana", "Necesito una cotización."));

		var outcome = await NotifyAsync(_queue.Jobs[0]);

		Assert.Equal(NotifyOutcome.Notified, outcome);
		Assert.Equal(LeadStatus.Notified, _store.Leads[0].Status);
		var email = Assert.Single(_notifier.Sent);
		Assert.Equal("help@acme.test", email.To);
		Assert.Equal("ana@example.com", email.ReplyTo);
		Assert.Equal("Ana", email.ReplyToName);
		Assert.StartsWith("[Asistente]", email.Subject, StringComparison.Ordinal);
		Assert.DoesNotContain("cotización", email.Subject, StringComparison.Ordinal);
		Assert.Contains("Necesito una cotización.", email.Body, StringComparison.Ordinal);
		Assert.Contains("¿Hacen envíos a Guatemala?", email.Body, StringComparison.Ordinal);
		Assert.True(
			email.Body.IndexOf("¿Hacen envíos", StringComparison.Ordinal) < email.Body.IndexOf("No encuentro", StringComparison.Ordinal),
			"The transcript reads oldest first.");
	}

	[Fact]
	public async Task Notify_WithoutAConversation_SaysSo()
	{
		await HandoffAsync(new HandoffRequest(null, "ana@example.com", null, "Hola"));

		await NotifyAsync(_queue.Jobs[0]);

		Assert.Contains("sin haber conversado", Assert.Single(_notifier.Sent).Body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Notify_WhenTheMailServerFails_RetriesLaterWithBackoff_ThenGivesUp()
	{
		_options.MaxAttempts = 3;
		_notifier.FailWith = new IOException("SMTP connection refused");
		await HandoffAsync(new HandoffRequest(null, "ana@example.com", null, "Hola"));
		var job = _queue.Jobs[0];
		var lead = _store.Leads[0];

		Assert.Equal(NotifyOutcome.WillRetry, await NotifyAsync(job));
		Assert.Equal(Start + _options.RetryDelay, lead.NextAttemptAt);
		Assert.Contains("connection refused", lead.LastError, StringComparison.Ordinal);

		// Not due yet: a sweep that finds it early does nothing.
		Assert.Equal(NotifyOutcome.Skipped, await NotifyAsync(job));

		_clock.Advance(_options.RetryDelay);
		Assert.Equal(NotifyOutcome.WillRetry, await NotifyAsync(job));
		Assert.Equal(_clock.GetUtcNow() + (_options.RetryDelay * 2), lead.NextAttemptAt);

		_clock.Advance(_options.RetryDelay * 2);
		Assert.Equal(NotifyOutcome.Failed, await NotifyAsync(job));
		Assert.Equal(LeadStatus.Failed, lead.Status);
		Assert.Equal(3, lead.Attempts);
	}

	[Fact]
	public void RetryDelay_DoublesUpToTheCap()
	{
		var settings = new HandoffOptions { RetryDelay = TimeSpan.FromMinutes(1), MaxRetryDelay = TimeSpan.FromMinutes(5) };

		Assert.Equal(
			[1, 2, 4, 5, 5],
			Enumerable.Range(1, 5).Select(attempt => NotifyLead.RetryDelay(attempt, settings).TotalMinutes));
	}

	[Fact]
	public async Task ConversationLog_RecordsBothSides_AndStartsOverOnAnUnknownId()
	{
		var context = new TenantContext();
		var log = new ConversationLog(context, new ConversationStore(_store, context), Options.Create(new ChatAnswerOptions()), _clock);

		var conversation = await log.BeginTurnAsync(_tenant.Id, Guid.NewGuid(), "Is shipping free?", CancellationToken.None);
		AnswerEvent[] answer =
		[
			new AnswerDelta("Yes [1]."),
			new AnswerCompleted("Yes [1].", AnswerOutcome.Answered, [new AnswerSource(1, Guid.NewGuid(), "Shipping", "shipping.md", null, "Free…")], false, 0.9, "hash", null),
		];
		await foreach (var _ in log.RecordAsync(conversation, answer.ToAsyncEnumerable(), CancellationToken.None))
		{
		}

		Assert.Single(_store.Conversations);
		Assert.Equal(["Is shipping free?", "Yes [1]."], _store.Messages.Select(m => m.Text));
		var recorded = _store.Messages[1];
		Assert.Equal(nameof(AnswerOutcome.Answered), recorded.Outcome);
		Assert.Equal(new MessageCitation(1, "Shipping", "shipping.md"), Assert.Single(recorded.Citations));
	}

	private sealed class ManualClock(DateTimeOffset start) : TimeProvider
	{
		private DateTimeOffset _now = start;

		public override DateTimeOffset GetUtcNow() => _now;

		public void Advance(TimeSpan by) => _now += by;
	}

	/// <summary>The rows behind both repositories; each repository sees only its bound tenant's, like the query filter.</summary>
	private sealed class InMemoryHandoffStore
	{
		public List<Lead> Leads { get; } = [];

		public List<Conversation> Conversations { get; } = [];

		public List<ConversationMessage> Messages { get; } = [];
	}

	private sealed class LeadStore(InMemoryHandoffStore store, ITenantContext tenant) : ILeadRepository
	{
		private IEnumerable<Lead> Visible => store.Leads.Where(l => l.TenantId == tenant.CurrentTenantId);

		public Task<Lead?> GetAsync(LeadId id, CancellationToken cancellationToken) =>
			Task.FromResult(Visible.SingleOrDefault(l => l.Id == id));

		public Task<Lead?> FindDuplicateAsync(string visitorEmail, string message, DateTimeOffset since, CancellationToken cancellationToken) =>
			Task.FromResult(Visible.LastOrDefault(l => l.VisitorEmail == visitorEmail && l.Message == message && l.CreatedAt >= since));

		public Task<IReadOnlyList<Lead>> ListAsync(int limit, CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<Lead>>([.. Visible.Reverse().Take(limit)]);

		public void Add(Lead lead) => store.Leads.Add(lead);

		public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
	}

	private sealed class ConversationStore(InMemoryHandoffStore store, ITenantContext tenant) : IConversationRepository
	{
		public Task<Conversation?> GetAsync(ConversationId id, CancellationToken cancellationToken) =>
			Task.FromResult(store.Conversations.SingleOrDefault(c => c.Id == id && c.TenantId == tenant.CurrentTenantId));

		public void Add(Conversation conversation) => store.Conversations.Add(conversation);

		public void Add(ConversationMessage message) => store.Messages.Add(message);

		public Task<IReadOnlyList<ConversationMessage>> ListRecentMessagesAsync(ConversationId id, int limit, CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<ConversationMessage>>(
				[.. store.Messages.Where(m => m.ConversationId == id && m.TenantId == tenant.CurrentTenantId).TakeLast(limit)]);

		public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
	}

	private sealed class LeadQueueSpy : ILeadQueue
	{
		public List<LeadJob> Jobs { get; } = [];

		public ValueTask EnqueueAsync(LeadJob job, CancellationToken cancellationToken)
		{
			Jobs.Add(job);
			return ValueTask.CompletedTask;
		}
	}

	private sealed class NotifierSpy : IHandoffNotifier
	{
		public List<HandoffEmail> Sent { get; } = [];

		public Exception? FailWith { get; set; }

		public Task SendAsync(HandoffEmail email, CancellationToken cancellationToken)
		{
			if (FailWith is not null)
			{
				throw FailWith;
			}

			Sent.Add(email);
			return Task.CompletedTask;
		}
	}
}
