namespace DocsAssistant.Application.Abstractions;

/// <summary>
/// A versioned prompt. The hash is recorded with every answer, so a change in quality or cost can be
/// traced to the prompt revision that caused it. The canary, when present, is a random marker inside
/// the instructions that must never appear in an answer: if it does, the prompt is leaking.
/// </summary>
public sealed record PromptTemplate(string Name, string Text, string Hash, string? Canary = null);

/// <summary>Prompts live as files next to the adapters, not as string literals inside use cases.</summary>
public interface IPromptLibrary
{
	PromptTemplate Answer { get; }
}
