using System.Text;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace V380Cameras.App;

/// <summary>
/// Wraps camera passwords with an AES-GCM key that lives in the Android
/// Keystore, so the key material is never inside the app's files and cannot be
/// exported even from a rooted device without the device unlock secret.
///
/// If the Keystore is unavailable (some emulators, or a corrupted keystore) it
/// degrades to app-private storage: the file still lives in the app sandbox,
/// but the value is not encrypted. The marker prefix records which happened so
/// a value is never silently misread.
/// </summary>
public static class SecretProtector
{
    private const string Alias = "v380-camera-secrets";
    private const string EncryptedPrefix = "enc:v1:";
    private const string PlainPrefix = "plain:v1:";
    private const int IvBytes = 12;
    private const int TagBits = 128;

    private static readonly object Gate = new();
    private static bool keystoreBroken;

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        lock (Gate)
        {
            if (!keystoreBroken)
            {
                try
                {
                    var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
                    cipher.Init(CipherMode.EncryptMode, GetOrCreateKey());
                    var iv = cipher.GetIV() ?? new byte[IvBytes];
                    var sealedBytes = cipher.DoFinal(Encoding.UTF8.GetBytes(plaintext)) ?? Array.Empty<byte>();

                    var packed = new byte[iv.Length + sealedBytes.Length];
                    Array.Copy(iv, 0, packed, 0, iv.Length);
                    Array.Copy(sealedBytes, 0, packed, iv.Length, sealedBytes.Length);
                    return EncryptedPrefix + Convert.ToBase64String(packed);
                }
                catch (Exception)
                {
                    keystoreBroken = true;
                }
            }

            return PlainPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        }
    }

    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";

        if (stored.StartsWith(PlainPrefix, StringComparison.Ordinal))
            return Encoding.UTF8.GetString(Convert.FromBase64String(stored[PlainPrefix.Length..]));

        if (!stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            // Older builds wrote the bare password; treat it as plaintext and it
            // gets re-wrapped on the next save.
            return stored;
        }

        try
        {
            var packed = Convert.FromBase64String(stored[EncryptedPrefix.Length..]);
            if (packed.Length <= IvBytes) return "";
            var iv = packed.AsSpan(0, IvBytes).ToArray();
            var body = packed.AsSpan(IvBytes).ToArray();

            var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            cipher.Init(CipherMode.DecryptMode, GetOrCreateKey(), new GCMParameterSpec(TagBits, iv));
            var plain = cipher.DoFinal(body);
            return plain == null ? "" : Encoding.UTF8.GetString(plain);
        }
        catch (Exception)
        {
            // A keystore reset (or a restored backup) makes old secrets unreadable.
            // Ask the user for the password again instead of failing to start.
            return "";
        }
    }

    private static IKey GetOrCreateKey()
    {
        var store = KeyStore.GetInstance("AndroidKeyStore")!;
        store.Load(null);

        var existing = store.GetKey(Alias, null);
        if (existing != null) return existing;

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes!, "AndroidKeyStore")!;
        var spec = new KeyGenParameterSpec.Builder(
                Alias,
                KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm!)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone!)
            .SetKeySize(256)
            .Build();
        generator.Init(spec);
        return generator.GenerateKey()!;
    }
}
