using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Conversations;
using DocsAssistant.Domain.Handoff;

namespace DocsAssistant.Domain.Tests;

public sealed class LeadTests
{
	private const int MaxAttempts = 3;
	private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
	private static readonly TimeSpan Delay = TimeSpan.FromMinutes(1);
	private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

	private static Lead NewLead() =>
		Lead.Create(TenantId.New(), conversationId: null, "ana@example.com", "Ana", "Call me back, please.", ContentLanguage.English, Now);

	[Fact]
	public void Create_IsPendingAndDueImmediately()
	{
		var lead = NewLead();

		Assert.Equal(LeadStatus.Pending, lead.Status);
		Assert.True(lead.IsDue(Now));
		Assert.Equal(0, lead.Attempts);
	}

	[Fact]
	public void HappyPath_PendingSendingNotified()
	{
		var lead = NewLead();

		lead.StartSending(Now);
		lead.MarkNotified(Now);

		Assert.Equal(LeadStatus.Notified, lead.Status);
		Assert.Equal(1, lead.Attempts);
		Assert.Equal(Now, lead.NotifiedAt);
		Assert.False(lead.IsDue(Now));
	}

	[Fact]
	public void FailureBeforeTheLimit_IsDueAgainOnlyAfterTheDelay()
	{
		var lead = NewLead();
		lead.StartSending(Now);

		var willRetry = lead.RecordFailure("SMTP 421", MaxAttempts, Delay, Now);

		Assert.True(willRetry);
		Assert.Equal(LeadStatus.Pending, lead.Status);
		Assert.Equal("SMTP 421", lead.LastError);
		Assert.False(lead.IsDue(Now + Delay - TimeSpan.FromSeconds(1)));
		Assert.True(lead.IsDue(Now + Delay));
		Assert.Throws<DomainException>(() => lead.StartSending(Now));
	}

	[Fact]
	public void FailureAtTheLimit_IsFailedAndKept()
	{
		var lead = NewLead();
		var at = Now;
		for (var i = 1; i < MaxAttempts; i++)
		{
			lead.StartSending(at);
			Assert.True(lead.RecordFailure("down", MaxAttempts, Delay, at));
			at += Delay;
		}

		lead.StartSending(at);
		var willRetry = lead.RecordFailure("down", MaxAttempts, Delay, at);

		Assert.False(willRetry);
		Assert.Equal(LeadStatus.Failed, lead.Status);
		Assert.Equal(MaxAttempts, lead.Attempts);
		Assert.False(lead.IsDue(at + TimeSpan.FromDays(1)));
	}

	[Fact]
	public void ExpiredSendingClaim_IsReleased_AndTheAttemptCounts()
	{
		var lead = NewLead();
		lead.StartSending(Now);

		Assert.False(lead.ReleaseExpiredClaim(Now + Lease - TimeSpan.FromSeconds(1), Lease, MaxAttempts));
		Assert.True(lead.ReleaseExpiredClaim(Now + Lease, Lease, MaxAttempts));

		Assert.Equal(LeadStatus.Pending, lead.Status);
		Assert.Equal(1, lead.Attempts);
		Assert.True(lead.IsDue(Now + Lease));
	}

	[Fact]
	public void NotifiedOrPending_CannotBeMarkedTwice()
	{
		var lead = NewLead();

		Assert.Throws<DomainException>(() => lead.MarkNotified(Now));
		lead.StartSending(Now);
		lead.MarkNotified(Now);
		Assert.Throws<DomainException>(() => lead.MarkNotified(Now));
		Assert.Throws<DomainException>(() => lead.RecordFailure("late", MaxAttempts, Delay, Now));
	}

	[Fact]
	public void BlankName_IsStoredAsNoName()
	{
		var lead = Lead.Create(TenantId.New(), null, "ana@example.com", "  ", "Hi there", ContentLanguage.Spanish, Now);

		Assert.Null(lead.VisitorName);
	}
}

public sealed class ConversationTests
{
	private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void Messages_BelongToTheConversationAndItsTenant_AndMoveItsClock()
	{
		var conversation = Conversation.Start(TenantId.New(), Now);
		var later = Now.AddMinutes(2);

		var question = conversation.AddVisitorMessage("  Is shipping free?  ", Now);
		var answer = conversation.AddAssistantMessage("Over 50 dollars [1].", "Answered", [new MessageCitation(1, "Shipping", "shipping.md")], later);

		Assert.Equal("Is shipping free?", question.Text);
		Assert.Equal(MessageRole.Visitor, question.Role);
		Assert.Null(question.Outcome);
		Assert.Equal(MessageRole.Assistant, answer.Role);
		Assert.Equal("Answered", answer.Outcome);
		Assert.Single(answer.Citations);
		Assert.All([question, answer], m => Assert.Equal(conversation.Id, m.ConversationId));
		Assert.All([question, answer], m => Assert.Equal(conversation.TenantId, m.TenantId));
		Assert.Equal(later, conversation.LastMessageAt);
	}

	[Fact]
	public void OverlongText_IsTruncatedToTheColumnSize()
	{
		var conversation = Conversation.Start(TenantId.New(), Now);

		var message = conversation.AddVisitorMessage(new string('a', ConversationMessage.MaxTextLength + 50), Now);

		Assert.Equal(ConversationMessage.MaxTextLength, message.Text.Length);
		Assert.EndsWith("…", message.Text, StringComparison.Ordinal);
	}
}
