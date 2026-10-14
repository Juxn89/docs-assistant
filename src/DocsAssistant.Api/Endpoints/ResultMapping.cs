using DocsAssistant.Application.Common;

namespace DocsAssistant.Api.Endpoints;

/// <summary>The single place where use-case errors become HTTP. Endpoints never pick status codes ad hoc.</summary>
internal static class ResultMapping
{
	public static IResult ToProblem(this Error error)
	{
		ArgumentNullException.ThrowIfNull(error);

		var status = error.Kind switch
		{
			ErrorKind.Validation => StatusCodes.Status400BadRequest,
			ErrorKind.NotFound => StatusCodes.Status404NotFound,
			ErrorKind.Conflict => StatusCodes.Status409Conflict,
			ErrorKind.Upstream => StatusCodes.Status502BadGateway,
			_ => StatusCodes.Status500InternalServerError,
		};

		return Results.Problem(
			statusCode: status,
			title: error.Message,
			type: $"https://github.com/Juxn89/docs-assistant/blob/main/docs/errors.md#{error.Code.Replace('.', '-')}",
			extensions: new Dictionary<string, object?> { ["code"] = error.Code });
	}

	public static IResult ToHttp<T>(this Result<T> result, Func<T, IResult> onSuccess)
	{
		ArgumentNullException.ThrowIfNull(result);
		ArgumentNullException.ThrowIfNull(onSuccess);
		return result.IsSuccess ? onSuccess(result.Value) : result.Error!.ToProblem();
	}
}
