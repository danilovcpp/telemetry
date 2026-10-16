using System.Security.Cryptography;
using System.Text;

namespace DeviceTelemetry.Application.Devices;

/// <summary>
/// Per-device API keys. Keys are 256-bit random values, so an unsalted SHA-256 hash is sufficient for storage
/// (there is nothing to brute-force); only the hash is persisted.
/// </summary>
public static class DeviceApiKey
{
    private const string Prefix = "dtk_";

    public static string Generate() =>
        Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Hash(string apiKey) => SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));

    /// <summary>Constant-time comparison of a presented key with the stored hash.</summary>
    public static bool Verify(string? presentedKey, byte[]? storedHash) =>
        !string.IsNullOrEmpty(presentedKey)
        && storedHash is { Length: SHA256.HashSizeInBytes }
        && CryptographicOperations.FixedTimeEquals(Hash(presentedKey), storedHash);
}
