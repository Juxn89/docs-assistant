using System.Reflection;

namespace DocsAssistant.Architecture.Tests;

/// <summary>
/// The inner layers carry no framework dependencies, checked on the references the compiler actually
/// emitted. Reflection, not ArchUnitNET: a namespace rule against an assembly that is not loaded
/// resolves to zero types and passes no matter what (verified in talent-mcp by injecting EF Core into
/// the domain — the ArchUnitNET rules stayed green).
/// </summary>
public sealed class ForbiddenAssemblyReferences
{
	private static readonly string[] PersistenceAndTransport =
	[
		"Microsoft.EntityFrameworkCore",
		"Npgsql",
		"Pgvector",
		"Microsoft.AspNetCore",
		"StackExchange.Redis",
		"MailKit",
	];

	/// <summary>Concrete AI clients. Application may use the Microsoft.Extensions.AI abstractions (the ports).</summary>
	private static readonly string[] AiClients =
	[
		"Anthropic",
		"Microsoft.ML.OnnxRuntime",
		"Microsoft.ML.Tokenizers",
	];

	private static readonly string[] Hosting =
	[
		"Microsoft.Extensions.DependencyInjection",
		"Microsoft.Extensions.Hosting",
		"Microsoft.Extensions.Options",
		"Microsoft.Extensions.AI",
		"OpenTelemetry",
	];

	[Fact]
	public void Domain_ReferencesNoFrameworkAssembly() =>
		AssertNoReferencesStartingWith(
			Layers.Domain,
			[.. PersistenceAndTransport, .. AiClients, .. Hosting],
			"the domain is plain C#: chunking, fusion and state machines run without Docker or a model.");

	[Fact]
	public void Application_ReferencesOnlyPorts() =>
		AssertNoReferencesStartingWith(
			Layers.Application,
			[.. PersistenceAndTransport, .. AiClients],
			"use cases are written against ports (ITenantContext, IChatClient, IEmbeddingGenerator), "
			+ "so they can be tested with fakes and the model provider can change by configuration.");

	private static void AssertNoReferencesStartingWith(Assembly assembly, string[] forbidden, string why)
	{
		var offending = assembly.GetReferencedAssemblies()
			.Select(reference => reference.Name ?? string.Empty)
			.Where(name => forbidden.Any(prefix => IsPrefixMatch(name, prefix)))
			.ToArray();

		Assert.True(
			offending.Length == 0,
			$"{assembly.GetName().Name} must not reference [{string.Join(", ", offending)}]: {why}");
	}

	/// <summary>"Microsoft.Extensions.AI" must not match "Microsoft.Extensions.AI.Abstractions".</summary>
	private static bool IsPrefixMatch(string name, string prefix) =>
		name.Equals(prefix, StringComparison.OrdinalIgnoreCase)
		|| (name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)
			&& !name.EndsWith(".Abstractions", StringComparison.OrdinalIgnoreCase));
}
