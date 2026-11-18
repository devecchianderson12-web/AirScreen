using System;
using System.IO;
using System.Security.Cryptography;

namespace DualScreen.Core;

/// <summary>
/// Protects secrets at rest with Windows DPAPI (CurrentUser scope). Nothing secret is ever written in plain text.
/// </summary>
public static class SecretStore
{
    private static readonly byte[] Entropy = "DualScreen.v1.secret-store"u8.ToArray();

    public static byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
    public static byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);

    public static void WriteProtected(string path, byte[] data)
    {
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, Protect(data));
        File.Move(tmp, path, overwrite: true);
    }

    public static byte[]? ReadProtected(string path)
    {
        if (!File.Exists(path)) return null;
        try { return Unprotect(File.ReadAllBytes(path)); }
        catch (CryptographicException ex)
        {
            Log.Warn($"Cannot decrypt {Path.GetFileName(path)} (different Windows user?): {ex.Message}");
            return null;
        }
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static bool FixedTimeEqualsHex(string a, string b)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(a), Convert.FromHexString(b)); }
        catch { return false; }
    }
}
