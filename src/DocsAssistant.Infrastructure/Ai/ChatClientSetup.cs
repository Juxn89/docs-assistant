using System.ComponentModel.DataAnnotations;
using Anthropic;
using DocsAssistant.Application.Common;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace DocsAssistant.Infrastructure.Ai;

public enum ChatProvider
{
	Anthropic = 0,

	/// <summary>Offline: a local model through Ollama. No key, no cost, lower quality.</summary>
	Ollama = 1,

	/// <summary>No language model: answers quote the most relevant sentences (extractive). Zero cost.</summary>
	None = 2,
}

/// <summary>Bound from "Ai". Model ids are configuration, never literals in use cases.</summary>
public sealed class AiOptions
{
	public const string SectionName = "Ai";

	public ChatProvider Provider { get; set; } = ChatProvider.Anthropic;

	/// <summary>Haiku: the answer is already in the passages, the model only has to phrase it and cite.</summary>
	[Required]
	public string AnswerModel { get; set; } = "claude-haiku-5-5";

	/// <summary>Falls back to the ANTHROPIC_API_KEY environment variable. Never commit a value here.</summary>
	public string? AnthropicApiKey { get; set; }

	/// <summary>
	/// Retries of failed model calls (connection errors, timeouts, 408/409/429/5xx). Null keeps the SDK
	/// default. The paid evals set 0: a retried attempt can be billed twice, and they stop at the first error.
	/// </summary>
	[Range(0, 10)]
	public int? MaxRetries { get; set; }

	[Required]
	public Uri OllamaEndpoint { get; set; } = new("http://localhost:11434");

	[Required]
	public string OllamaModel { get; set; } = "qwen2.5:3b";

	/// <summary>Activity source of the model calls, so they appear in the same traces as the HTTP request.</summary>
	public const string TelemetrySource = "DocsAssistant.Ai";
}

internal static class ChatClientSetup
{
	private const string AnthropicKeyVariable = "ANTHROPIC_API_KEY";

	public static IChatClient Create(AiOptions options)
	{
		IChatClient inner = options.Provider switch
		{
			ChatProvider.Ollama => new OllamaApiClient(options.OllamaEndpoint, options.OllamaModel),
			ChatProvider.None => new UnconfiguredChatClient(),
			_ => CreateAnthropic(options),
		};

		return new ChatClientBuilder(inner)
			.UseOpenTelemetry(sourceName: AiOptions.TelemetrySource)
			.Build();
	}

	private static IChatClient CreateAnthropic(AiOptions options)
	{
		var key = options.AnthropicApiKey ?? Environment.GetEnvironmentVariable(AnthropicKeyVariable);
		return string.IsNullOrWhiteSpace(key)
			? new UnconfiguredChatClient()
			: CreateAnthropic(options, key, httpClient: null);
	}

	/// <summary>The HTTP client is injectable so a test can capture the exact request without a network call.</summary>
	internal static IChatClient CreateAnthropic(AiOptions options, string key, HttpClient? httpClient)
	{
		IAnthropicClient client = new AnthropicClient { ApiKey = key };
		if (options.MaxRetries is { } retries)
		{
			client = client.WithOptions(o => o with { MaxRetries = retries });
		}

		if (httpClient is not null)
		{
			client = client.WithOptions(o => o with { HttpClient = httpClient });
		}

		return client.AsIChatClient(options.AnswerModel);
	}
}

/// <summary>
/// Stands in when no model is configured, so the rest of the API (ingestion, search, admin) still
/// starts and works. Asking a question fails with a clear, typed error instead of a null reference.
/// </summary>
internal sealed class UnconfiguredChatClient : IChatClient
{
	private const string Message =
		"No language model is configured (Ai:Provider=None, or Anthropic without a key). Answers fall back to extractive mode.";

	public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
		throw new AssistantUnavailableException(Message);

	public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
		throw new AssistantUnavailableException(Message);

	public object? GetService(Type serviceType, object? serviceKey = null) => null;

	public void Dispose()
	{
	}
}
