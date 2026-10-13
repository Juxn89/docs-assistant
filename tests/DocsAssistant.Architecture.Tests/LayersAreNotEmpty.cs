namespace DocsAssistant.Architecture.Tests;

/// <summary>
/// Every negative rule uses WithoutRequiringPositiveResults(), so an EMPTY layer satisfies all of
/// them. This keeps the suite from going green while asserting nothing.
/// </summary>
public sealed class LayersAreNotEmpty
{
	public static TheoryData<string> LayerNames =>
	[
		Layers.Domain.GetName().Name!,
		Layers.Application.GetName().Name!,
		Layers.Infrastructure.GetName().Name!,
		Layers.Api.GetName().Name!,
	];

	[Theory]
	[MemberData(nameof(LayerNames))]
	public void Layer_IsLoadedAndHasTypes(string assemblyName)
	{
		var types = LayerDependencyRules.Architecture.Types
			.Count(type => type.Assembly.Name.StartsWith(assemblyName + ",", StringComparison.Ordinal)
				|| type.Assembly.Name == assemblyName);

		Assert.True(types > 0, $"{assemblyName} contributed no types to the architecture under test.");
	}

	[Fact]
	public void ForbiddenReferenceCheck_DetectsARealReference()
	{
		// The Infrastructure assembly genuinely references EF Core; if this ever reads as empty, the
		// reflection check has stopped seeing references and every rule above is vacuous.
		var names = Layers.Infrastructure.GetReferencedAssemblies().Select(a => a.Name);

		Assert.Contains("Microsoft.EntityFrameworkCore", names);
	}
}
