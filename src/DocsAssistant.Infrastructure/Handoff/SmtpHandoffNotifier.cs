using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using DocsAssistant.Application.Abstractions;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace DocsAssistant.Infrastructure.Handoff;

public enum EmailSecurity
{
	/// <summary>Plain SMTP. Only for a local catcher such as Mailpit.</summary>
	None = 0,
	StartTls = 1,
	SslOnConnect = 2,
}

/// <summary>
/// Bound from "Email". With no host the API still runs: leads are stored and listed, and the sender
/// stays idle until a server is configured. Under the AppHost the host comes from Mailpit.
/// </summary>
public sealed class EmailOptions
{
	public const string SectionName = "Email";

	/// <summary>Aspire's Mailpit resource name; its connection string is "endpoint=smtp://host:port".</summary>
	public const string MailpitConnectionName = "mail";

	public string? Host { get; set; }

	[Range(1, 65535)]
	public int Port { get; set; } = 587;

	public EmailSecurity Security { get; set; } = EmailSecurity.StartTls;

	public string? UserName { get; set; }

	public string? Password { get; set; }

	/// <summary>The sender the operator sees. The visitor is the Reply-To, never the From: their domain did not send it.</summary>
	[EmailAddress]
	public string FromAddress { get; set; } = "assistant@docs-assistant.local";

	public string FromName { get; set; } = "Docs Assistant";

	[Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
	public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

	[MemberNotNullWhen(true, nameof(Host))]
	public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);

	/// <summary>Fills host and port from Aspire's Mailpit connection string when no host was set explicitly.</summary>
	internal void UseMailpit(string? connectionString)
	{
		if (IsConfigured || string.IsNullOrWhiteSpace(connectionString))
		{
			return;
		}

		var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
		if (builder.TryGetValue("endpoint", out var value) && Uri.TryCreate(value?.ToString(), UriKind.Absolute, out var endpoint))
		{
			Host = endpoint.Host;
			Port = endpoint.Port;
			Security = EmailSecurity.None;
		}
	}
}

/// <summary>
/// SMTP through MailKit. One connection per email: leads are rare, and a pooled connection would only
/// add a way to fail (servers drop idle connections).
/// </summary>
internal sealed class SmtpHandoffNotifier(IOptions<EmailOptions> options) : IHandoffNotifier
{
	public async Task SendAsync(HandoffEmail email, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(email);
		var settings = options.Value;
		if (!settings.IsConfigured)
		{
			throw new InvalidOperationException("No SMTP server is configured (Email:Host).");
		}

		using var message = new MimeMessage();
		message.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
		message.To.Add(MailboxAddress.Parse(email.To));
		message.ReplyTo.Add(new MailboxAddress(email.ReplyToName ?? string.Empty, email.ReplyTo));
		message.Subject = email.Subject;
		message.Body = new TextPart(MimeKit.Text.TextFormat.Plain) { Text = email.Body };

		using var client = new SmtpClient { Timeout = (int)settings.Timeout.TotalMilliseconds };
		await client.ConnectAsync(settings.Host, settings.Port, Map(settings.Security), cancellationToken).ConfigureAwait(false);
		if (!string.IsNullOrEmpty(settings.UserName))
		{
			await client.AuthenticateAsync(settings.UserName, settings.Password ?? string.Empty, cancellationToken).ConfigureAwait(false);
		}

		await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
		await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
	}

	private static SecureSocketOptions Map(EmailSecurity security) => security switch
	{
		EmailSecurity.None => SecureSocketOptions.None,
		EmailSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
		_ => SecureSocketOptions.StartTls,
	};
}
