using System.Net;
using System.Text;
using System.Text.Json;
using DocsAssistant.Application.Features.Chat;
using DocsAssistant.Infrastructure.Ai;
using Microsoft.Extensions.AI;

namespace DocsAssistant.Integration.Tests;

/// <summary>
/// The request the answer pipeline sends to Claude, captured by a fake HTTP handler: no network, no
/// key, no cost. Claude Haiku 5.5 rejects sampling parameters and assistant prefill with a 400 and
/// thinks by default, so these are checked here rather than discovered on a paid call.
/// </summary>
public sealed class AnthropicRequestShapeTests
{
	private const string Model = "claude-haiku-5-5";

	// A recorded-shape stream: an empty (omitted) thinking block before the text, as Haiku 5.5 sends
	// when thinking is on. The answer must contain only the text.
	private const string Stream = """
		event: message_start
		data: {"type":"message_start","message":{"id":"msg_test","type":"message","role":"assistant","model":"claude-haiku-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":2800,"output_tokens":1}}}

		event: content_block_start
		data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

		event: content_block_delta
		data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"c2lnbmF0dXJl"}}

		event: content_block_stop
		data: {"type":"content_block_stop","index":0}

		event: content_block_start
		data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

		event: content_block_delta
		data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Shipping is free "}}

		event: content_block_delta
		data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"over $40 [1]."}}

		event: content_block_stop
		data: {"type":"content_block_stop","index":1}

		event: message_delta
		data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":12}}

		event: message_stop
		data: {"type":"message_stop"}


		""";

	[Fact]
	public async Task AnswerRequest_MatchesWhatHaiku55Accepts()
	{
		var (body, _, _) = await SendAsync();

		Assert.Equal(Model, body.GetProperty("model").GetString());
		Assert.Equal(new ChatAnswerOptions().MaxOutputTokens, body.GetProperty("max_tokens").GetInt32());
		Assert.True(body.GetProperty("stream").GetBoolean());

		// ReasoningEffort.None is sent as thinking disabled: no thinking tokens are billed. Haiku 5.5
		// accepts it at its default effort (medium); it would 400 at xhigh or max.
		Assert.Equal("disabled", body.GetProperty("thinking").GetProperty("type").GetString());
		Assert.False(body.TryGetProperty("output_config", out var config) && config.TryGetProperty("effort", out _));

		foreach (var sampling in new[] { "temperature", "top_p", "top_k" })
		{
			Assert.False(body.TryGetProperty(sampling, out _), $"{sampling} returns a 400 on Haiku 5.5");
		}

		var messages = body.GetProperty("messages").EnumerateArray().ToList();
		Assert.Equal("user", messages[^1].GetProperty("role").GetString());
		Assert.Contains("NO_ANSWER", SystemText(body), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Stream_YieldsOnlyTheTextAndReportsUsageAndStopReason()
	{
		var (_, text, response) = await SendAsync();

		Assert.Equal("Shipping is free over $40 [1].", text);
		Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
		Assert.Equal(2800, response.Usage!.InputTokenCount);
		Assert.Equal(12, response.Usage.OutputTokenCount);
	}

	private static async Task<(JsonElement Body, string Text, ChatResponse Response)> SendAsync()
	{
		var handler = new CapturingHandler(Stream);
		using var http = new HttpClient(handler);
		var chat = ChatClientSetup.CreateAnthropic(new AiOptions { AnswerModel = Model, MaxRetries = 0 }, "test-key-not-a-secret", http);

		var settings = new ChatAnswerOptions();
		var options = new ChatOptions
		{
			Instructions = "Answer from the documents. If they do not contain the answer, reply NO_ANSWER.",
			MaxOutputTokens = settings.MaxOutputTokens,
			Reasoning = new ReasoningOptions { Effort = settings.ReasoningEffort },
		};

		var updates = new List<ChatResponseUpdate>();
		await foreach (var update in chat.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "<question>Is shipping free?</question>")], options))
		{
			updates.Add(update);
		}

		var response = updates.ToChatResponse();
		using var document = JsonDocument.Parse(handler.Body!);
		return (document.RootElement.Clone(), response.Text, response);
	}

	private static string SystemText(JsonElement body)
	{
		var system = body.GetProperty("system");
		return system.ValueKind == JsonValueKind.String
			? system.GetString()!
			: string.Concat(system.EnumerateArray().Select(b => b.GetProperty("text").GetString()));
	}

	private sealed class CapturingHandler(string stream) : HttpMessageHandler
	{
		public string? Body { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Body = await request.Content!.ReadAsStringAsync(cancellationToken);
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(stream.Replace("\r\n", "\n", StringComparison.Ordinal), Encoding.UTF8, "text/event-stream"),
			};
		}
	}
}
