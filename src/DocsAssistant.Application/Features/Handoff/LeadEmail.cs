using System.Globalization;
using System.Text;
using DocsAssistant.Application.Abstractions;
using DocsAssistant.Domain.Common;
using DocsAssistant.Domain.Conversations;
using DocsAssistant.Domain.Handoff;
using DocsAssistant.Domain.Tenants;

namespace DocsAssistant.Application.Features.Handoff;

/// <summary>
/// The operator's email: who wrote, what they want, and what they already asked the assistant, so the
/// reply does not start with "how can I help you?". Written in the tenant's language; Reply-To is the
/// visitor, so answering is one click. Plain text only — everything in it came from a visitor.
/// </summary>
public static class LeadEmail
{
	public static HandoffEmail Compose(Tenant tenant, Lead lead, IReadOnlyList<ConversationMessage> transcript)
	{
		ArgumentNullException.ThrowIfNull(tenant);
		ArgumentNullException.ThrowIfNull(lead);
		ArgumentNullException.ThrowIfNull(transcript);

		var t = tenant.DefaultLanguage == ContentLanguage.Spanish ? Texts.Spanish : Texts.English;
		var from = lead.VisitorName is null ? lead.VisitorEmail : $"{lead.VisitorName} <{lead.VisitorEmail}>";

		var body = new StringBuilder()
			.AppendLine(t.Intro)
			.AppendLine()
			.AppendLine(CultureInfo.InvariantCulture, $"{t.From}: {from}")
			.AppendLine(t.ReplyHint)
			.AppendLine()
			.AppendLine(CultureInfo.InvariantCulture, $"{t.Message}:")
			.AppendLine(lead.Message)
			.AppendLine();

		if (transcript.Count == 0)
		{
			body.AppendLine(t.NoConversation);
		}
		else
		{
			body.AppendLine(CultureInfo.InvariantCulture, $"{t.Conversation}:");
			foreach (var message in transcript)
			{
				var who = message.Role == MessageRole.Visitor ? t.Visitor : t.Assistant;
				body.AppendLine()
					.AppendLine(CultureInfo.InvariantCulture, $"[{message.CreatedAt:yyyy-MM-dd HH:mm} UTC] {who}:")
					.AppendLine(message.Text);
				foreach (var citation in message.Citations)
				{
					body.AppendLine(CultureInfo.InvariantCulture, $"  [{citation.Number}] {citation.Title} — {citation.Source}");
				}
			}
		}

		body.AppendLine()
			.AppendLine("--")
			.AppendLine(CultureInfo.InvariantCulture, $"{tenant.Name} · lead {lead.Id}");

		// The subject carries the visitor's address (validated, single line) but none of their free text.
		return new HandoffEmail(tenant.HandoffEmail, lead.VisitorEmail, lead.VisitorName, $"{t.Subject} {lead.VisitorEmail}", body.ToString());
	}

	private sealed record Texts(
		string Subject,
		string Intro,
		string From,
		string ReplyHint,
		string Message,
		string Conversation,
		string NoConversation,
		string Visitor,
		string Assistant)
	{
		public static readonly Texts English = new(
			"[Assistant] A visitor wants to talk to a person:",
			"A visitor asked the assistant to pass their question to a person.",
			"From",
			"Reply to this email to answer them directly.",
			"Their message",
			"What they asked the assistant",
			"They wrote without chatting with the assistant first.",
			"Visitor",
			"Assistant");

		public static readonly Texts Spanish = new(
			"[Asistente] Un visitante quiere hablar con una persona:",
			"Un visitante pidió al asistente que pasara su consulta a una persona.",
			"De",
			"Responde a este correo para contestarle directamente.",
			"Su mensaje",
			"Lo que preguntó al asistente",
			"Escribió sin haber conversado antes con el asistente.",
			"Visitante",
			"Asistente");
	}
}
