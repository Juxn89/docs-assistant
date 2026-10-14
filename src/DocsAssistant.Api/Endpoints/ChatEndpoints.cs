using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using DocsAssistant.Application.Abstractions;
using DocsAssistant.Application.Common;
using DocsAssistant.Application.Features.Chat;
using Microsoft.Net.Http.Headers;

namespace DocsAssistant.Api.Endpoints;

/// <summary>
/// The public endpoint the widget calls. Identified by the tenant's public key (it grants nothing on
/// its own) and allowed only from the tenant's registered origins.
/// </summary>
internal static partial class ChatEndpoints
{
	/// <summary>SSE event names: the widget's contract.</summary>
	internal static class Events
	{
		public const string Delta = "delta";
		public const string Done = "done";
		public const string Error = "error";
	}

	public static RouteGroupBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
	{
		var chat = app.MapGroup("/v1/chat").WithTags("Chat");
		chat.MapPost("/{publicKey}", AskAsync);
		return chat;
	}

	private static async Task<IResult> AskAsync(
		string publicKey,
		ChatRequest request,
		HttpContext http,
		ITenantRepository tenants,
		AskQuestion ask,
		ILoggerFactory loggers,
		CancellationToken cancellationToken)
	{
		var tenant = await tenants.GetByPublicKeyAsync(publicKey, cancellationToken);
		if (tenant is null)
		{
			return Error.NotFound("tenant.not_found", "Unknown assistant key.").ToProblem();
		}

		// Browsers always send Origin on a cross-site POST; server-to-server callers may omit it.
		var origin = http.Request.Headers[HeaderNames.Origin].ToString();
		if (!string.IsNullOrEmpty(origin) && !tenant.IsOriginAllowed(origin))
		{
			return Results.Problem(
				statusCode: StatusCodes.Status403Forbidden,
				title: "This site is not allowed to use this assistant.",
				extensions: new Dictionary<string, object?> { ["code"] = "chat.origin_not_allowed" });
		}

		if (string.IsNullOrWhiteSpace(request.Question))
		{
			return Error.Validation("chat.question_required", "Ask a question.").ToProblem();
		}

		var stream = ask.HandleAsync(tenant.Id, request.Question, tenant.DefaultLanguage, cancellationToken);
		return TypedResults.ServerSentEvents(ToSse(stream, loggers.CreateLogger(typeof(ChatEndpoints)), cancellationToken));
	}

	/// <summary>
	/// Once streaming has started the status code is already 200, so a failure becomes a final, typed
	/// "error" event the widget can show — never a half-written answer that just stops.
	/// </summary>
	private static async IAsyncEnumerable<SseItem<object>> ToSse(
		IAsyncEnumerable<AnswerEvent> events,
		ILogger logger,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var enumerator = events.GetAsyncEnumerator(cancellationToken);
		while (true)
		{
			SseItem<object> item;
			try
			{
				if (!await enumerator.MoveNextAsync())
				{
					yield break;
				}

				item = enumerator.Current switch
				{
					AnswerDelta delta => new SseItem<object>(new { text = delta.Text }, Events.Delta),
					AnswerCompleted done => new SseItem<object>(DoneEvent.From(done), Events.Done),
					var other => throw new InvalidOperationException($"Unknown answer event {other.GetType().Name}."),
				};
			}
			catch (AssistantUnavailableException exception)
			{
				LogUnavailable(logger, exception);
				item = new SseItem<object>(new { code = "assistant.unavailable", message = "The assistant is temporarily unavailable." }, Events.Error);
			}

			yield return item;
			if (item.EventType == Events.Error)
			{
				yield break;
			}
		}
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "Assistant unavailable")]
	private static partial void LogUnavailable(ILogger logger, Exception exception);

	internal sealed record ChatRequest(string Question);

	/// <summary>The final event: what to render under the answer. Internal fields (similarity, hash) stay internal.</summary>
	internal sealed record DoneEvent(string Outcome, bool OfferHandoff, IReadOnlyList<DoneCitation> Citations)
	{
		public static DoneEvent From(AnswerCompleted done) => new(
			done.Outcome.ToString(),
			done.OfferHandoff,
			[.. done.Citations.Select(c => new DoneCitation(c.Number, c.DocumentTitle, c.SourceUri, c.Heading, c.Excerpt))]);
	}

	internal sealed record DoneCitation(int Number, string Title, string Source, string? Section, string Excerpt);
}
