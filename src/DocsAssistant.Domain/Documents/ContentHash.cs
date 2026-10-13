using System.Security.Cryptography;

namespace DocsAssistant.Domain.Documents;

/// <summary>
/// SHA-256 of a document's raw bytes, lower-case hex. It is the idempotency key of ingestion:
/// uploading the same file twice to the same tenant resolves to the existing document.
/// </summary>
public sealed record ContentHash
{
	/// <summary>SHA-256 is 32 bytes: 64 hex characters.</summary>
	public const int HexLength = 64;

	private ContentHash(string value) => Value = value;

	public string Value { get; }

	public static ContentHash Compute(ReadOnlySpan<byte> content) =>
		new(Convert.ToHexStringLower(SHA256.HashData(content)));

	public static ContentHash FromHex(string hex)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(hex);
		if (hex.Length != HexLength || !hex.All(Uri.IsHexDigit))
		{
			throw new ArgumentException("A content hash is 64 hexadecimal characters.", nameof(hex));
		}

		return new ContentHash(hex.ToLowerInvariant());
	}

	public override string ToString() => Value;
}
