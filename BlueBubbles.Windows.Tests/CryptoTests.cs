using BlueBubbles.Core.Utils;

namespace BlueBubbles.Windows.Tests;

public class CryptoUtilsTests
{
    [Fact]
    public void EncryptDecrypt_RoundTrip_ReturnsOriginal()
    {
        var plainText = "Hello, BlueBubbles!";
        var passphrase = "test-password-123";

        var encrypted = CryptoUtils.EncryptAESCryptoJS(plainText, passphrase);
        var decrypted = CryptoUtils.DecryptAESCryptoJS(encrypted, passphrase);

        Assert.Equal(plainText, decrypted);
    }

    [Fact]
    public void EncryptDecrypt_EmptyString_RoundTrips()
    {
        var encrypted = CryptoUtils.EncryptAESCryptoJS("", "pass");
        var decrypted = CryptoUtils.DecryptAESCryptoJS(encrypted, "pass");

        Assert.Equal("", decrypted);
    }

    [Fact]
    public void EncryptDecrypt_JsonPayload_RoundTrips()
    {
        var json = """{"status":200,"message":"pong","data":{"os_version":"14.0"}}""";
        var passphrase = "my-server-password";

        var encrypted = CryptoUtils.EncryptAESCryptoJS(json, passphrase);
        var decrypted = CryptoUtils.DecryptAESCryptoJS(encrypted, passphrase);

        Assert.Equal(json, decrypted);
    }

    [Fact]
    public void EncryptDecrypt_UnicodeContent_RoundTrips()
    {
        var plainText = "Hello 🌎 世界 مرحبا";
        var passphrase = "password";

        var encrypted = CryptoUtils.EncryptAESCryptoJS(plainText, passphrase);
        var decrypted = CryptoUtils.DecryptAESCryptoJS(encrypted, passphrase);

        Assert.Equal(plainText, decrypted);
    }

    [Fact]
    public void Encrypt_ProducesSaltedPrefix()
    {
        var encrypted = CryptoUtils.EncryptAESCryptoJS("test", "pass");
        var bytes = Convert.FromBase64String(encrypted);

        Assert.True(bytes.Length >= 16);
        Assert.Equal((byte)'S', bytes[0]);
        Assert.Equal((byte)'a', bytes[1]);
        Assert.Equal((byte)'l', bytes[2]);
        Assert.Equal((byte)'t', bytes[3]);
        Assert.Equal((byte)'e', bytes[4]);
        Assert.Equal((byte)'d', bytes[5]);
        Assert.Equal((byte)'_', bytes[6]);
        Assert.Equal((byte)'_', bytes[7]);
    }

    [Fact]
    public void Encrypt_ProducesNonZeroSalt()
    {
        var encrypted = CryptoUtils.EncryptAESCryptoJS("test", "pass");
        var bytes = Convert.FromBase64String(encrypted);
        var salt = bytes[8..16];

        Assert.All(salt, b => Assert.NotEqual(0, b));
    }

    [Fact]
    public void Encrypt_DifferentCallsProduceDifferentOutput()
    {
        var a = CryptoUtils.EncryptAESCryptoJS("test", "pass");
        var b = CryptoUtils.EncryptAESCryptoJS("test", "pass");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Decrypt_WrongPassword_Throws()
    {
        var encrypted = CryptoUtils.EncryptAESCryptoJS("secret data", "correct-password");

        Assert.ThrowsAny<Exception>(() =>
            CryptoUtils.DecryptAESCryptoJS(encrypted, "wrong-password"));
    }

    [Fact]
    public void EncryptDecrypt_DifferentPassphrases_ProduceDifferentCiphertext()
    {
        var plainText = "same content";
        var a = CryptoUtils.EncryptAESCryptoJS(plainText, "password-A");
        var b = CryptoUtils.EncryptAESCryptoJS(plainText, "password-B");

        // Different passphrases produce different ciphertext (even ignoring salt differences)
        Assert.NotEqual(a, b);
        // But each decrypts with its own passphrase
        Assert.Equal(plainText, CryptoUtils.DecryptAESCryptoJS(a, "password-A"));
        Assert.Equal(plainText, CryptoUtils.DecryptAESCryptoJS(b, "password-B"));
    }

    [Fact]
    public void EncryptDecrypt_LargePayload_RoundTrips()
    {
        var plainText = new string('A', 10_000);
        var passphrase = "test-pass";

        var encrypted = CryptoUtils.EncryptAESCryptoJS(plainText, passphrase);
        var decrypted = CryptoUtils.DecryptAESCryptoJS(encrypted, passphrase);

        Assert.Equal(plainText, decrypted);
    }
}
