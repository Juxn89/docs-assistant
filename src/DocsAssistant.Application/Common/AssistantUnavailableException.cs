namespace DocsAssistant.Application.Common;

/// <summary>
/// The language model cannot be used right now (not configured, budget exhausted, provider down).
/// The API reports it as a typed error event, distinct from "I don't know".
/// </summary>
public sealed class AssistantUnavailableException : Exception
{
	public AssistantUnavailableException(string message)
		: base(message)
	{
	}

	public AssistantUnavailableException(string message, Exception innerException)
		: base(message, innerException)
	{
	}

	public AssistantUnavailableException()
	{
	}
}
