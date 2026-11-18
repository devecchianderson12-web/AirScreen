using System;
using System.Security.Cryptography;
using DualScreen.Update;
using Xunit;

public class UpdateSignatureTests
{
    [Fact]
    public void AcceptsValidAndRejectsTamperedOrForeignSignatures()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = key.ExportSubjectPublicKeyInfoPem();
        byte[] file = RandomNumberGenerator.GetBytes(4096);
        byte[] hash = SHA256.HashData(file);
        // Same format as scripts/sign-update.sh (openssl dgst -sha256 -sign => DER).
        string sig = Convert.ToBase64String(key.SignHash(hash, DSASignatureFormat.Rfc3279DerSequence));

        Assert.True(UpdateService.VerifySignature(hash, sig, pem));

        file[0] ^= 1;
        Assert.False(UpdateService.VerifySignature(SHA256.HashData(file), sig, pem));

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(UpdateService.VerifySignature(hash, sig, other.ExportSubjectPublicKeyInfoPem()));
        Assert.False(UpdateService.VerifySignature(hash, "not-base64!", pem));
    }

    [Fact]
    public void VerifiesOpenSslSignatureFromReleaseScript()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ds-sigtest");
        string pubPath = System.IO.Path.Combine(dir, "p.pem");
        if (!System.IO.File.Exists(pubPath)) return; // produced by the shell test in CI; optional locally
        byte[] hash = SHA256.HashData(System.IO.File.ReadAllBytes(System.IO.Path.Combine(dir, "DualScreen.exe")));
        string sig = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "DualScreen.exe.sig"));
        Assert.True(UpdateService.VerifySignature(hash, sig, System.IO.File.ReadAllText(pubPath)));
    }
}
