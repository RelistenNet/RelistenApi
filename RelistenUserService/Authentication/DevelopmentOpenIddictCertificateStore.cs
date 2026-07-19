using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RelistenUserService.Authentication;

internal static class DevelopmentOpenIddictCertificateStore
{
    private const string CertificateDirectoryName = ".local-openiddict";

    public static X509Certificate2 LoadSigningCertificate(IHostEnvironment environment) =>
        LoadOrCreate(environment, "signing");

    public static X509Certificate2 LoadEncryptionCertificate(IHostEnvironment environment) =>
        LoadOrCreate(environment, "encryption");

    private static X509Certificate2 LoadOrCreate(
        IHostEnvironment environment,
        string purpose)
    {
        var directory = Path.Combine(environment.ContentRootPath, CertificateDirectoryName);
        var path = Path.Combine(directory, $"{purpose}.pfx");

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(directory);
            CreateCertificate(path, purpose);
        }

        // Loading the checkout-local PFX gives every issuer process the same key
        // material without relying on a certificate previously persisted in Keychain.
        return X509CertificateLoader.LoadPkcs12FromFile(
            path,
            password: null);
    }

    private static void CreateCertificate(string path, string purpose)
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(
            $"CN=Relisten local OpenIddict {purpose}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false,
            hasPathLengthConstraint: false,
            pathLengthConstraint: 0,
            critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(
            request.PublicKey,
            critical: false));

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(5));
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            File.WriteAllBytes(temporaryPath, certificate.Export(X509ContentType.Pkcs12));
            try
            {
                File.Move(temporaryPath, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another local issuer won the first-start race. Its complete,
                // atomically moved certificate is the shared development key.
            }
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
