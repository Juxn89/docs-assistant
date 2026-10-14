using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DocsAssistant.Application.Abstractions;

namespace DocsAssistant.Infrastructure.Ai;

/// <summary>
/// Prompts are embedded resources, loaded once. The short hash identifies the revision in logs and in
/// stored answers; changing a single character of a prompt changes it.
/// </summary>
internal sealed class PromptLibrary : IPromptLibrary
{
	private const int HashLength = 12;

	public PromptLibrary() => Answer = Load("answer");

	public PromptTemplate Answer { get; }

	private static PromptTemplate Load(string name)
	{
		var assembly = typeof(PromptLibrary).Assembly;
		var resource = $"{assembly.GetName().Name}.Ai.Prompts.{name}.md";
		using var stream = assembly.GetManifestResourceStream(resource)
			?? throw new InvalidOperationException($"Prompt resource '{resource}' is missing from {assembly.GetName().Name}.");
		using var reader = new StreamReader(stream, Encoding.UTF8);
		var text = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
		var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..HashLength];
		return new PromptTemplate(name, text, hash);
	}
}
