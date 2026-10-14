namespace DocsAssistant.Application.Abstractions;

/// <summary>
/// A versioned prompt. The hash is recorded with every answer, so a change in quality or cost can be
/// traced to the prompt revision that caused it.
/// </summary>
public sealed record PromptTemplate(string Name, string Text, string Hash);

/// <summary>Prompts live as files next to the adapters, not as string literals inside use cases.</summary>
public interface IPromptLibrary
{
	PromptTemplate Answer { get; }
}
