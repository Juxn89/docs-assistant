using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using ArchitectureModel = ArchUnitNET.Domain.Architecture;

namespace DocsAssistant.Architecture.Tests;

/// <summary>
/// The inter-layer half of the dependency rule: Domain ← Application ← Infrastructure ← Api.
/// Every rule compares two of this repo's own assemblies, so both sides are loaded and a violation is
/// actually detected. Rules about framework namespaces live in <see cref="ForbiddenAssemblyReferences"/>,
/// because the ArchUnitNET form of those passes silently when the framework is not loaded.
/// </summary>
public sealed class LayerDependencyRules
{
	internal static readonly ArchitectureModel Architecture = new ArchLoader()
		.LoadAssemblies(Layers.Domain, Layers.Application, Layers.Infrastructure, Layers.Api)
		.Build();

	private static readonly IObjectProvider<IType> DomainLayer =
		Types().That().ResideInAssembly(Layers.Domain).As("Domain");

	private static readonly IObjectProvider<IType> ApplicationLayer =
		Types().That().ResideInAssembly(Layers.Application).As("Application");

	private static readonly IObjectProvider<IType> InfrastructureLayer =
		Types().That().ResideInAssembly(Layers.Infrastructure).As("Infrastructure");

	private static readonly IObjectProvider<IType> ApiLayer =
		Types().That().ResideInAssembly(Layers.Api).As("Api");

	[Fact]
	public void Domain_DependsOnNoOtherLayer() =>
		Types().That().Are(DomainLayer)
			.Should().NotDependOnAny(ApplicationLayer)
			.AndShould().NotDependOnAny(InfrastructureLayer)
			.AndShould().NotDependOnAny(ApiLayer)
			.WithoutRequiringPositiveResults()
			.Check(Architecture);

	[Fact]
	public void Application_DependsOnlyOnDomain() =>
		Types().That().Are(ApplicationLayer)
			.Should().NotDependOnAny(InfrastructureLayer)
			.AndShould().NotDependOnAny(ApiLayer)
			.WithoutRequiringPositiveResults()
			.Check(Architecture);

	[Fact]
	public void Infrastructure_DoesNotDependOnApi() =>
		Types().That().Are(InfrastructureLayer)
			.Should().NotDependOnAny(ApiLayer)
			.WithoutRequiringPositiveResults()
			.Check(Architecture);
}
