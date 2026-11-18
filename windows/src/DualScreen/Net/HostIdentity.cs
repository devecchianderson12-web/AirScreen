using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DualScreen.Core;

namespace DualScreen.Net;

/// <summary>
/// The PC's long-term identity: a self-signed ECDSA P-256 certificate used for TLS (LAN) and for signing
/// WebRTC offers (remote). Phones pin its SHA-256 fingerprint at pairing time. Persisted as a DPAPI-protected PFX.
/// </summary>
public sealed class HostIdentity : IDisposable
{
    public X509Certificate2 Certificate { get; }
    /// <summary>Lowercase hex SHA-256 of the certificate DER.</summary>
    public string Fingerprint { get; }
    /// <summary>Stable public identifier: first 16 bytes of SHA-256(SubjectPublicKeyInfo), hex.</summary>
    public string HostId { get; }

    private HostIdentity(X509Certificate2 cert)
    {
        Certificate = cert;
        Fingerprint = SecretStore.Sha256Hex(cert.RawData);
        byte[] spki = cert.PublicKey.ExportSubjectPublicKeyInfo();
        HostId = Convert.ToHexString(SHA256.HashData(spki), 0, 16).ToLowerInvariant();
    }

    public static HostIdentity LoadOrCreate()
    {
        byte[]? pfx = SecretStore.ReadProtected(AppPaths.IdentityFile);
        if (pfx != null)
        {
            try
            {
                var cert = new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
                if (cert.HasPrivateKey && cert.NotAfter > DateTime.Now.AddDays(30))
                    return new HostIdentity(cert);
            }
            catch (Exception ex) { Log.Warn($"Identity unreadable, regenerating: {ex.Message}"); }
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN=DualScreen Host {Environment.MachineName}", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        // Schannel cannot use ephemeral keys: round-trip through PFX so the key lives in a CNG container.
        byte[] export = ephemeral.Export(X509ContentType.Pfx);
        SecretStore.WriteProtected(AppPaths.IdentityFile, export);
        var loaded = new X509Certificate2(export, (string?)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        Log.Info("Generated new host identity");
        return new HostIdentity(loaded);
    }

    /// <summary>ECDSA-P256/SHA-256 signature (IEEE P1363 r||s), base64.</summary>
    public string Sign(string text)
    {
        using var ecdsa = Certificate.GetECDsaPrivateKey() ?? throw new InvalidOperationException("No private key");
        return Convert.ToBase64String(ecdsa.SignData(System.Text.Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256));
    }

    public string CertificateBase64 => Convert.ToBase64String(Certificate.RawData);

    public void Dispose() => Certificate.Dispose();
}
