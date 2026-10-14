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
			: new AnthropicClient { ApiKey = key }.AsIChatClient(options.AnswerModel);
	}
}

/// <summary>
/// Stands in when no model is configured, so the rest of the API (ingestion, search, admin) still
/// starts and works. Asking a question fails with a clear, typed error instead of a null reference.
/// </summary>
internal sealed class UnconfiguredChatClient : IChatClient
{
	private const string Message =
		"No language model is configured. Set Ai:AnthropicApiKey (or ANTHROPIC_API_KEY), or Ai:Provider=Ollama.";

	public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
		throw new AssistantUnavailableException(Message);

	public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
		throw new AssistantUnavailableException(Message);

	public object? GetService(Type serviceType, object? serviceKey = null) => null;

	public void Dispose()
	{
	}
}
