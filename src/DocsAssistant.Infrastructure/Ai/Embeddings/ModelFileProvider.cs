using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace DocsAssistant.Infrastructure.Ai.Embeddings;

/// <summary>
/// Makes a pinned file available locally: reuse it if the cached copy matches its hash, otherwise
/// download to a temporary file, verify, and only then move it into place. A half-downloaded or
/// tampered file can never be loaded.
/// </summary>
internal sealed partial class ModelFileProvider(HttpClient http, ILogger<ModelFileProvider> logger)
{
	public async Task<string> EnsureAsync(Uri url, string sha256, string cacheDirectory, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(url);
		Directory.CreateDirectory(cacheDirectory);

		var target = Path.Combine(cacheDirectory, $"{sha256}{Path.GetExtension(url.AbsolutePath)}");
		if (File.Exists(target) && await HashOfAsync(target, cancellationToken).ConfigureAwait(false) == sha256)
		{
			return target;
		}

		LogDownloading(logger, url);
		var temporary = target + $".{Guid.NewGuid():N}.partial";
		try
		{
			using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
			{
				response.EnsureSuccessStatusCode();
				await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
				await using var file = File.Create(temporary);
				await source.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
			}

			var actual = await HashOfAsync(temporary, cancellationToken).ConfigureAwait(false);
			if (actual != sha256)
			{
				throw new InvalidDataException($"{url} has SHA-256 {actual}, expected {sha256}. Refusing to load it.");
			}

			File.Move(temporary, target, overwrite: true);
			return target;
		}
		finally
		{
			if (File.Exists(temporary))
			{
				File.Delete(temporary);
			}
		}
	}

	private static async Task<string> HashOfAsync(string path, CancellationToken cancellationToken)
	{
		await using var stream = File.OpenRead(path);
		return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Downloading embedding model file {Url}")]
	private static partial void LogDownloading(ILogger logger, Uri url);
}
