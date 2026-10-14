namespace DocsAssistant.Application.Common;

/// <summary>
/// Another unit of work changed the same row first. Persistence translates its own optimistic
/// concurrency exception into this, so use cases can react without referencing EF Core.
/// </summary>
public sealed class ConcurrencyConflictException : Exception
{
	public ConcurrencyConflictException(string message, Exception innerException)
		: base(message, innerException)
	{
	}

	public ConcurrencyConflictException(string message)
		: base(message)
	{
	}

	public ConcurrencyConflictException()
	{
	}
}
