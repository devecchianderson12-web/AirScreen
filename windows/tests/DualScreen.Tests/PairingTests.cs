using System;
using DualScreen.Net;
using Xunit;

/// <summary>Uses DPAPI-backed storage, so it only runs on Windows (CI).</summary>
public class PairingTests
{
    [Fact]
    public void FullPairingFlow()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var id = HostIdentity.LoadOrCreate();
        var p = new PairingService(id);
        string code = p.NewCode();
        string deviceId = "test-" + Guid.NewGuid();
        string nonce = "0123456789abcdef0123";

        var bad = new HelloMsg { DeviceId = deviceId, Auth = new AuthBlock { Mode = "pair", Nonce = nonce, Proof = PairingService.ComputeProof("000000", id.Fingerprint, deviceId, nonce) } };
        if (code != "000000") Assert.Equal(PairingService.AuthResult.BadCode, p.Authenticate(bad, out _));

        var good = new HelloMsg { DeviceId = deviceId, DeviceName = "Phone", Auth = new AuthBlock { Mode = "pair", Nonce = nonce, Proof = PairingService.ComputeProof(code, id.Fingerprint, deviceId, nonce) } };
        Assert.Equal(PairingService.AuthResult.Paired, p.Authenticate(good, out string? token));
        Assert.NotNull(token);
        Assert.Null(p.CurrentCode); // single use
        Assert.Equal(PairingService.AuthResult.CodeExpired, p.Authenticate(good, out _));

        var tok = new HelloMsg { DeviceId = deviceId, DeviceName = "Phone", Auth = new AuthBlock { Mode = "token", Token = token } };
        Assert.Equal(PairingService.AuthResult.Ok, p.Authenticate(tok, out _));
        tok.Auth.Token = token + "x";
        Assert.Equal(PairingService.AuthResult.BadToken, p.Authenticate(tok, out _));

        p.Revoke(deviceId);
        tok.Auth.Token = token;
        Assert.Equal(PairingService.AuthResult.BadToken, p.Authenticate(tok, out _));
    }

    [Fact]
    public void LocksCodeAfterTooManyAttempts()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var id = HostIdentity.LoadOrCreate();
        var p = new PairingService(id);
        string code = p.NewCode();
        string wrong = code == "111111" ? "222222" : "111111";
        var hello = new HelloMsg { DeviceId = "d", Auth = new AuthBlock { Mode = "pair", Nonce = "0123456789abcdef", Proof = PairingService.ComputeProof(wrong, id.Fingerprint, "d", "0123456789abcdef") } };
        for (int i = 0; i < 4; i++) Assert.Equal(PairingService.AuthResult.BadCode, p.Authenticate(hello, out _));
        Assert.Equal(PairingService.AuthResult.TooManyAttempts, p.Authenticate(hello, out _));
        Assert.Null(p.CurrentCode);
    }
}
