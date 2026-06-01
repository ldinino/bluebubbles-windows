using System.Security.Cryptography;
using System.Text;

namespace BlueBubbles.Core.Utils;

/// <summary>
/// CryptoJS-compatible AES-256-CBC encryption/decryption with MD5-based key derivation (EVP_BytesToKey).
/// </summary>
public static class CryptoUtils
{
    public static string EncryptAESCryptoJS(string plainText, string passphrase)
    {
        var salt = new byte[8];
        for (int i = 0; i < salt.Length; i++)
            salt[i] = (byte)RandomNumberGenerator.GetInt32(1, 246);

        var (key, iv) = DeriveKeyAndIV(passphrase, salt);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        byte[] encrypted;
        using (var encryptor = aes.CreateEncryptor(key, iv))
        {
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            encrypted = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        }

        var result = new byte[16 + encrypted.Length];
        Encoding.ASCII.GetBytes("Salted__").CopyTo(result, 0);
        salt.CopyTo(result, 8);
        encrypted.CopyTo(result, 16);

        return Convert.ToBase64String(result);
    }

    public static string DecryptAESCryptoJS(string encrypted, string passphrase)
    {
        var bytes = Convert.FromBase64String(encrypted);
        var salt = new byte[8];
        Buffer.BlockCopy(bytes, 8, salt, 0, 8);
        var ciphertext = new byte[bytes.Length - 16];
        Buffer.BlockCopy(bytes, 16, ciphertext, 0, ciphertext.Length);

        var (key, iv) = DeriveKeyAndIV(passphrase, salt);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor(key, iv);
        var decrypted = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
        return Encoding.UTF8.GetString(decrypted);
    }

    internal static (byte[] Key, byte[] IV) DeriveKeyAndIV(string passphrase, byte[] salt)
    {
        // Match Dart's createUint8ListFromString: low byte of each UTF-16 code unit (Latin-1)
        var password = new byte[passphrase.Length];
        for (int i = 0; i < passphrase.Length; i++)
            password[i] = (byte)passphrase[i];

        var concatenated = new byte[48];
        byte[] currentHash = [];
        int offset = 0;

        while (offset < 48)
        {
            byte[] preHash;
            if (currentHash.Length > 0)
            {
                preHash = new byte[currentHash.Length + password.Length + salt.Length];
                Buffer.BlockCopy(currentHash, 0, preHash, 0, currentHash.Length);
                Buffer.BlockCopy(password, 0, preHash, currentHash.Length, password.Length);
                Buffer.BlockCopy(salt, 0, preHash, currentHash.Length + password.Length, salt.Length);
            }
            else
            {
                preHash = new byte[password.Length + salt.Length];
                Buffer.BlockCopy(password, 0, preHash, 0, password.Length);
                Buffer.BlockCopy(salt, 0, preHash, password.Length, salt.Length);
            }

            currentHash = MD5.HashData(preHash);
            int toCopy = Math.Min(16, 48 - offset);
            Buffer.BlockCopy(currentHash, 0, concatenated, offset, toCopy);
            offset += 16;
        }

        return (concatenated[..32], concatenated[32..48]);
    }
}
