using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace RelistenUserService.Authentication;

/// <summary>
/// Persists local OpenIddict RSA keys without importing private keys through
/// macOS certificate APIs. The checkout-local files survive issuer restarts;
/// only ordinary managed RSA objects exist in the running process.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class DevelopmentOpenIddictRsaKeyStore
{
    private const string KeyDirectoryName = ".local-openiddict";

    public static RsaSecurityKey LoadSigningKey(IHostEnvironment environment) =>
        LoadOrCreate(environment, "signing");

    public static RsaSecurityKey LoadEncryptionKey(IHostEnvironment environment) =>
        LoadOrCreate(environment, "encryption");

    private static RsaSecurityKey LoadOrCreate(IHostEnvironment environment, string purpose)
    {
        var directory = Path.Combine(environment.ContentRootPath, KeyDirectoryName);
        var path = Path.Combine(directory, $"{purpose}.pk8");

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(directory);
            CreateKey(path);
        }
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var encodedKey = File.ReadAllBytes(path);
        var rsa = RSA.Create();
        try
        {
            rsa.ImportPkcs8PrivateKey(encodedKey, out var bytesRead);
            if (bytesRead != encodedKey.Length)
            {
                throw new CryptographicException(
                    $"The local OpenIddict {purpose} key contains trailing data.");
            }

            return new RsaSecurityKey(rsa)
            {
                KeyId = Convert.ToHexString(
                        SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()))
                    .ToLowerInvariant()
            };
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encodedKey);
        }
    }

    private static void CreateKey(string path)
    {
        using var rsa = RSA.Create(3072);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            var encodedKey = rsa.ExportPkcs8PrivateKey();
            try
            {
                var options = new FileStreamOptions
                {
                    Access = FileAccess.Write,
                    Mode = FileMode.CreateNew,
                    Share = FileShare.None,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                };
                using var stream = new FileStream(temporaryPath, options);
                stream.Write(encodedKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encodedKey);
            }

            try
            {
                File.Move(temporaryPath, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another local issuer atomically created the shared key first.
            }
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
