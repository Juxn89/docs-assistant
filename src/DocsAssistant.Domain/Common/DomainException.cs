namespace DocsAssistant.Domain.Common;

/// <summary>A business rule was violated. The API maps it to a 409/422 ProblemDetails, never to a 500.</summary>
public sealed class DomainException : Exception
{
	public DomainException(string message)
		: base(message)
	{
	}

	public DomainException(string message, Exception innerException)
		: base(message, innerException)
	{
	}

	public DomainException()
	{
	}
}
