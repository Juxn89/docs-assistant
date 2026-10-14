using DocsAssistant.Infrastructure.Handoff;

namespace DocsAssistant.Integration.Tests;

/// <summary>Under the AppHost the SMTP server arrives as Aspire's Mailpit connection string, not as Email:Host.</summary>
public sealed class EmailOptionsTests
{
	[Theory]
	[InlineData("endpoint=smtp://localhost:31025")]
	[InlineData("Endpoint=smtp://localhost:31025")]
	public void MailpitConnectionString_FillsHostAndPort_WithoutTls(string connectionString)
	{
		var options = new EmailOptions();

		options.UseMailpit(connectionString);

		Assert.True(options.IsConfigured);
		Assert.Equal("localhost", options.Host);
		Assert.Equal(31025, options.Port);
		Assert.Equal(EmailSecurity.None, options.Security);
	}

	[Fact]
	public void ExplicitHost_WinsOverMailpit()
	{
		var options = new EmailOptions { Host = "smtp.example.com", Port = 587 };

		options.UseMailpit("endpoint=smtp://localhost:31025");

		Assert.Equal("smtp.example.com", options.Host);
		Assert.Equal(EmailSecurity.StartTls, options.Security);
	}

	[Fact]
	public void NothingConfigured_LeavesTheSenderIdle()
	{
		var options = new EmailOptions();

		options.UseMailpit(null);

		Assert.False(options.IsConfigured);
	}
}
