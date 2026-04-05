using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EasArchiver;

/// <summary>
/// Persists a password encrypted with Windows DPAPI (CurrentUser scope).
/// Only available on Windows — all methods are no-ops on other platforms.
/// </summary>
[SuppressMessage("Interoperability", "CA1416", Justification = "All DPAPI calls are guarded by OperatingSystem.IsWindows()")]
public static class CredentialService
{
    private static readonly string CredentialPath =
        Path.Combine(EasArchiver.AppDataDir, "credential.dat");

    /// <summary>
    /// Some entropy for security by obscurity
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EasArchiver.v1");

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static void Save(string password)
    {
        if (!IsSupported) return;
        Directory.CreateDirectory(EasArchiver.AppDataDir);
        var plain = Encoding.UTF8.GetBytes(password);
        var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(CredentialPath, encrypted);
    }

    public static string? Load()
    {
        if (!IsSupported) return null;
        if (!File.Exists(CredentialPath)) return null;
        try
        {
            var encrypted = File.ReadAllBytes(CredentialPath);
            var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    public static void Delete()
    {
        if (!IsSupported) return;
        if (File.Exists(CredentialPath))
            File.Delete(CredentialPath);
    }
}
