namespace DocsAssistant.Evals;

/// <summary>
/// How a model-backed suite runs: the meter that prices its calls (null when no model is billed), the
/// items it is limited to (null for all), and whether the model is replaced by a dry-run stand-in.
/// A subset or a dry run writes its results under its own name, so it never replaces a full run's.
/// </summary>
internal sealed record RunContext(Meter? Meter, IReadOnlySet<string>? Only, bool DryRun)
{
	public static readonly RunContext Free = new(null, null, false);

	public string Label(string provider) =>
		provider + (DryRun ? " dry run" : string.Empty) + (Only is null ? string.Empty : " subset");

	public bool Includes(string id) => Only is null || Only.Contains(id);
}
