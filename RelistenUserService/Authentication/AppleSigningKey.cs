using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using RelistenUserService.Configuration;

namespace RelistenUserService.Authentication;

internal static class AppleSigningKey
{
    public static ECDsaSecurityKey Load(AppleProviderOptions options)
    {
        if (!File.Exists(options.PrivateKeyPath))
        {
            throw new InvalidOperationException(
                $"The Apple private key file '{options.PrivateKeyPath}' does not exist."
            );
        }

        var algorithm = ECDsa.Create();
        try
        {
            // Apple supplies a PEM-encoded PKCS#8 key.
            algorithm.ImportFromPem(File.ReadAllText(options.PrivateKeyPath));
            return new ECDsaSecurityKey(algorithm) { KeyId = options.KeyId };
        }
        catch
        {
            algorithm.Dispose();
            throw;
        }
    }
}
