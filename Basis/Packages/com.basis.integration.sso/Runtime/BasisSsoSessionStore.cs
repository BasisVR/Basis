using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace Basis.Integration.Sso
{
    /// <summary>
    /// Persists a <see cref="BasisSsoSession"/> to <see cref="Application.persistentDataPath"/>,
    /// encrypted at rest with a key derived from a device-specific value. This is not OS keychain
    /// grade (the device value lives on the same machine), but it keeps refresh tokens out of
    /// plaintext on disk — the agreed trade-off for this round (see docs/sso-spec.md §7).
    ///
    /// On-disk layout (all one base64 blob):
    ///   [1] version | [16] IV | [32] HMAC-SHA256(version‖IV‖ciphertext) | [..] AES-256-CBC ciphertext
    /// Integrity is verified (encrypt-then-MAC) before any decryption. Any tampering, a changed
    /// device id, or a format bump yields "no session" and forces a fresh login.
    /// </summary>
    public static class BasisSsoSessionStore
    {
        public const string FileName = "SsoSession.BAS";

        private const byte FormatVersion = 1;
        private const int IvSize = 16;
        private const int MacSize = 32;
        private const int KeySize = 32;
        private const int Pbkdf2Iterations = 100_000;

        // Fixed application salt. Combined with the per-device value so the derived key is
        // both device-bound and app-specific. Not a secret; integrity comes from the HMAC.
        private static readonly byte[] AppSalt = Encoding.UTF8.GetBytes("BasisVR.SSO.v1.session-key-salt");

        private static string FilePath => Path.Combine(Application.persistentDataPath, FileName);

        public static bool Exists => File.Exists(FilePath);

        public static void Save(BasisSsoSession session)
        {
            if (session == null) { Clear(); return; }
            try
            {
                string json = JsonConvert.SerializeObject(session);
                byte[] blob = Encrypt(Encoding.UTF8.GetBytes(json));
                File.WriteAllText(FilePath, Convert.ToBase64String(blob));
                BasisDebug.Log("[SSO] Session saved.");
            }
            catch (Exception e)
            {
                BasisDebug.LogWarning($"[SSO] Failed to save session: {e.Message}");
            }
        }

        /// <summary>Returns the decrypted session, or null if absent/corrupt/tampered.</summary>
        public static BasisSsoSession Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                byte[] blob = Convert.FromBase64String(File.ReadAllText(FilePath));
                if (!TryDecrypt(blob, out byte[] plaintext))
                {
                    BasisDebug.LogWarning("[SSO] Stored session failed integrity check; discarding.");
                    Clear();
                    return null;
                }
                BasisSsoSession session = JsonConvert.DeserializeObject<BasisSsoSession>(Encoding.UTF8.GetString(plaintext));
                if (session == null || session.Version != new BasisSsoSession().Version)
                {
                    Clear();
                    return null;
                }
                return session;
            }
            catch (Exception e)
            {
                BasisDebug.LogWarning($"[SSO] Failed to load session: {e.Message}");
                return null;
            }
        }

        public static void Clear()
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch (Exception e)
            {
                BasisDebug.LogWarning($"[SSO] Failed to clear session: {e.Message}");
            }
        }

        // ── Crypto ───────────────────────────────────────────────────────────

        private static void DeriveKeys(out byte[] aesKey, out byte[] macKey)
        {
            byte[] password = Encoding.UTF8.GetBytes(SystemInfo.deviceUniqueIdentifier ?? "unknown-device");
            using var kdf = new Rfc2898DeriveBytes(password, AppSalt, Pbkdf2Iterations, HashAlgorithmName.SHA256);
            aesKey = kdf.GetBytes(KeySize);
            macKey = kdf.GetBytes(KeySize);
        }

        private static byte[] Encrypt(byte[] plaintext)
        {
            DeriveKeys(out byte[] aesKey, out byte[] macKey);
            using var aes = Aes.Create();
            aes.KeySize = KeySize * 8;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = aesKey;
            aes.GenerateIV();
            byte[] iv = aes.IV;

            byte[] ciphertext;
            using (ICryptoTransform enc = aes.CreateEncryptor())
                ciphertext = enc.TransformFinalBlock(plaintext, 0, plaintext.Length);

            byte[] mac = ComputeMac(macKey, iv, ciphertext);

            byte[] output = new byte[1 + IvSize + MacSize + ciphertext.Length];
            output[0] = FormatVersion;
            Buffer.BlockCopy(iv, 0, output, 1, IvSize);
            Buffer.BlockCopy(mac, 0, output, 1 + IvSize, MacSize);
            Buffer.BlockCopy(ciphertext, 0, output, 1 + IvSize + MacSize, ciphertext.Length);
            return output;
        }

        private static bool TryDecrypt(byte[] blob, out byte[] plaintext)
        {
            plaintext = null;
            if (blob == null || blob.Length < 1 + IvSize + MacSize) return false;
            if (blob[0] != FormatVersion) return false;

            DeriveKeys(out byte[] aesKey, out byte[] macKey);

            byte[] iv = new byte[IvSize];
            Buffer.BlockCopy(blob, 1, iv, 0, IvSize);
            byte[] mac = new byte[MacSize];
            Buffer.BlockCopy(blob, 1 + IvSize, mac, 0, MacSize);
            int cipherLen = blob.Length - (1 + IvSize + MacSize);
            byte[] ciphertext = new byte[cipherLen];
            Buffer.BlockCopy(blob, 1 + IvSize + MacSize, ciphertext, 0, cipherLen);

            byte[] expected = ComputeMac(macKey, iv, ciphertext);
            if (!FixedTimeEquals(mac, expected)) return false;

            using var aes = Aes.Create();
            aes.KeySize = KeySize * 8;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = aesKey;
            aes.IV = iv;
            using (ICryptoTransform dec = aes.CreateDecryptor())
                plaintext = dec.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
            return true;
        }

        private static byte[] ComputeMac(byte[] macKey, byte[] iv, byte[] ciphertext)
        {
            using var hmac = new HMACSHA256(macKey);
            byte[] data = new byte[1 + iv.Length + ciphertext.Length];
            data[0] = FormatVersion;
            Buffer.BlockCopy(iv, 0, data, 1, iv.Length);
            Buffer.BlockCopy(ciphertext, 0, data, 1 + iv.Length, ciphertext.Length);
            return hmac.ComputeHash(data);
        }

        // Constant-time comparison, kept local so we don't depend on CryptographicOperations
        // being present in every Unity scripting runtime.
        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
