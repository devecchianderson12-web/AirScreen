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

public class UpdateMarkerTests
{
    [Fact]
    public void ExtractsHiddenMarkerAndCleansNotes()
    {
        string sha = new string('A', 64);
        string body = "### Añadido\n- Algo nuevo\n\n<!-- dualscreen-update sha256=" + sha + " sig=MEUCIQ+/abc= -->\n";
        var (notes, s, sig) = UpdateService.ParseUpdateMarker(body);
        Assert.Equal("### Añadido\n- Algo nuevo", notes);
        Assert.Equal(new string('a', 64), s);
        Assert.Equal("MEUCIQ+/abc=", sig);
    }

    [Fact]
    public void MissingMarkerMeansNotVerifiable()
    {
        var (notes, s, sig) = UpdateService.ParseUpdateMarker("Solo notas");
        Assert.Equal("Solo notas", notes);
        Assert.Null(s);
        Assert.Null(sig);
    }
}
