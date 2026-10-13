using System.Reflection;
using DocsAssistant.Application.Abstractions;
using DocsAssistant.Domain.Documents;
using DocsAssistant.Infrastructure.Persistence;

namespace DocsAssistant.Architecture.Tests;

/// <summary>
/// Assembly OBJECTS, not names: ArchUnitNET's ResideInAssembly(string) matches the full assembly
/// name, so a simple name matches nothing and every rule passes vacuously. Learned in talent-mcp.
/// </summary>
internal static class Layers
{
	public static readonly Assembly Domain = typeof(Document).Assembly;

	public static readonly Assembly Application = typeof(ITenantContext).Assembly;

	public static readonly Assembly Infrastructure = typeof(DocsAssistantDbContext).Assembly;

	public static readonly Assembly Api = typeof(Program).Assembly;
}
