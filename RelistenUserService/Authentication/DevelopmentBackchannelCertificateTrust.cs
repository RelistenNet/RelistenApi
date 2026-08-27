using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace RelistenUserService.Authentication;

public sealed class DevelopmentBackchannelCertificateTrust
{
    private readonly string _issuerOrigin;
    private readonly X509Certificate2 _certificateAuthority;

    public DevelopmentBackchannelCertificateTrust(
        Uri issuer,
        string certificateAuthorityPath)
    {
        _issuerOrigin = issuer.GetLeftPart(UriPartial.Authority);
        _certificateAuthority = X509CertificateLoader.LoadCertificateFromFile(
            certificateAuthorityPath);
    }

    public bool Validate(
        HttpRequestMessage request,
        X509Certificate2? certificate,
        X509Chain? ignoredChain,
        SslPolicyErrors errors)
    {
        if (request.RequestUri is null
            || !string.Equals(
                request.RequestUri.GetLeftPart(UriPartial.Authority),
                _issuerOrigin,
                StringComparison.Ordinal)
            || certificate is null
            || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors)
                != SslPolicyErrors.None)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_certificateAuthority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

        // The local client accepts only the configured development CA. A permissive
        // certificate callback would let an attacker intercept authorization codes.
        return chain.Build(certificate);
    }
}
