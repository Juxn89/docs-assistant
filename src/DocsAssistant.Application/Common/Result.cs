namespace DocsAssistant.Application.Common;

/// <summary>How the API should report a failure. Maps 1:1 to an HTTP status in the endpoint layer.</summary>
public enum ErrorKind
{
	Validation = 0,
	NotFound = 1,
	Conflict = 2,
	/// <summary>The request is fine but an upstream (a URL, a model) failed or refused.</summary>
	Upstream = 3,
}

/// <summary>An expected failure: something the caller can act on. Bugs are exceptions, not errors.</summary>
public sealed record Error(string Code, string Message, ErrorKind Kind)
{
	public static Error Validation(string code, string message) => new(code, message, ErrorKind.Validation);

	public static Error NotFound(string code, string message) => new(code, message, ErrorKind.NotFound);

	public static Error Conflict(string code, string message) => new(code, message, ErrorKind.Conflict);

	public static Error Upstream(string code, string message) => new(code, message, ErrorKind.Upstream);
}

public sealed class Result<T>
{
	private readonly T? _value;

	private Result(T? value, Error? error)
	{
		_value = value;
		Error = error;
	}

	public Error? Error { get; }

	public bool IsSuccess => Error is null;

	public T Value => IsSuccess
		? _value!
		: throw new InvalidOperationException($"Result is a failure ({Error!.Code}); there is no value.");

	public static Result<T> Success(T value) => new(value, null);

	public static Result<T> Failure(Error error) => new(default, error ?? throw new ArgumentNullException(nameof(error)));

#pragma warning disable CA2225 // Operator overloads have named alternates: Success/Failure are the named forms.
	public static implicit operator Result<T>(T value) => Success(value);

	public static implicit operator Result<T>(Error error) => Failure(error);
#pragma warning restore CA2225
}
