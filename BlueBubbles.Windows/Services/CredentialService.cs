using System.Security.Cryptography;
using System.Text;
using BlueBubbles.Core.Services;

namespace BlueBubbles.Windows.Services;

/// <summary>
/// Stores the server password encrypted at rest with Windows DPAPI
/// (<see cref="DataProtectionScope.CurrentUser"/>) in a file under LocalAppData. DPAPI works
/// for unpackaged apps, unlike <c>Windows.Security.Credentials.PasswordVault</c>, which requires
/// package identity. The ciphertext is bound to the current user account, so it cannot be
/// decrypted by another user or on another machine.
/// </summary>
public class CredentialService : ICredentialService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BlueBubbles", "credential.bin");

    public void SavePassword(string password)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(password), optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath, cipher);
    }

    public string? GetPassword()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var cipher = File.ReadAllBytes(FilePath);
            var plain = ProtectedData.Unprotect(cipher, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            // Corrupt blob, or written under a different user/machine — treat as no credential.
            return null;
        }
    }

    public void DeletePassword()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch { /* nothing to delete */ }
    }
}
