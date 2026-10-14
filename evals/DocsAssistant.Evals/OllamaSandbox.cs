using DotNet.Testcontainers.Builders;
using Testcontainers.Ollama;

namespace DocsAssistant.Evals;

/// <summary>
/// A local model for the evals, run in a container rather than installed on the host — with the same
/// hardening as deploy/ollama/compose.yaml: no Linux capabilities, no privilege escalation, read-only
/// root filesystem, bounded memory/CPU/processes, and its port bound to loopback only. Models live in
/// the shared named volume, so each is downloaded once for the evals and the compose stack alike.
/// </summary>
internal sealed class OllamaSandbox : IAsyncDisposable
{
	public const string Image = "ollama/ollama:0.40.4";
	public const string ModelVolume = "docs-assistant-ollama-models";

	private const string ModelDirectory = "/root/.ollama";
	private const long MemoryLimitBytes = 4L * 1024 * 1024 * 1024;
	private const long CpuLimitNanoCpus = 4_000_000_000;
	private const long ProcessLimit = 512;

	private readonly OllamaContainer _container;

	private OllamaSandbox(OllamaContainer container) => _container = container;

	public Uri Endpoint => new(_container.GetBaseAddress());

	public static async Task<OllamaSandbox> StartAsync(string model)
	{
		var container = new OllamaBuilder(Image)
			.WithVolumeMount(ModelVolume, ModelDirectory)
			.WithTmpfsMount("/tmp")
			.WithCreateParameterModifier(parameters =>
			{
				var host = parameters.HostConfig ??= new Docker.DotNet.Models.HostConfig();
				host.CapDrop = ["ALL"];
				host.SecurityOpt = ["no-new-privileges:true"];
				host.ReadonlyRootfs = true;
				host.Memory = MemoryLimitBytes;
				host.NanoCPUs = CpuLimitNanoCpus;
				host.PidsLimit = ProcessLimit;
				foreach (var binding in (host.PortBindings ?? new Dictionary<string, IList<Docker.DotNet.Models.PortBinding>>()).Values.SelectMany(bindings => bindings))
				{
					binding.HostIP = "127.0.0.1";
				}
			})
			.Build();

		await container.StartAsync();

		Console.WriteLine($"Ollama {Image} sandboxed; pulling {model} (cached in volume {ModelVolume})…");
		var pull = await container.ExecAsync(["ollama", "pull", model]);
		if (pull.ExitCode != 0)
		{
			await container.DisposeAsync();
			throw new InvalidOperationException($"ollama pull {model} failed: {pull.Stderr}");
		}

		return new OllamaSandbox(container);
	}

	public ValueTask DisposeAsync() => _container.DisposeAsync();
}
