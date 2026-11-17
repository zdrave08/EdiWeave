// EdiWeave.Next - LGPL-3.0, see LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdiWeave.Hipaa835;

namespace EdiWeave.Healthcare
{
    /// <summary>One line of the audit log.</summary>
    public sealed class AuditEntry
    {
        /// <summary>When it happened (UTC, ISO 8601).</summary>
        public string Time { get; set; }

        /// <summary>Who did it (a user name or ID, never a patient).</summary>
        public string User { get; set; }

        /// <summary>What was done, for example "claims.submit".</summary>
        public string Action { get; set; }

        /// <summary>What it was done to, for example a file name or a claim number.</summary>
        public string Resource { get; set; }

        /// <summary>The result, for example "sent" or "denied".</summary>
        public string Outcome { get; set; }

        /// <summary>Short detail. Must not contain patient data.</summary>
        public string Detail { get; set; }

        /// <summary>Hash of the previous line (empty for the first).</summary>
        public string PreviousHash { get; set; }

        /// <summary>Hash of this line, chained to the previous one. Changing or deleting a line breaks the chain.</summary>
        public string Hash { get; set; }
    }

    /// <summary>
    /// An append-only audit log (one JSON object per line). Each line contains the hash of the line before it, so
    /// <see cref="Verify"/> detects edited, removed or reordered lines. It records who did what and when, never patient data.
    /// </summary>
    public sealed class AuditLog
    {
        private readonly object _gate = new object();
        private readonly string _path;
        private readonly Func<DateTime> _clock;

        /// <summary>Creates the log at the given path.</summary>
        public AuditLog(string path, Func<DateTime> utcClock = null)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _clock = utcClock ?? (() => DateTime.UtcNow);
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        /// <summary>Appends an entry.</summary>
        public AuditEntry Record(string user, string action, string resource, string outcome, string detail)
        {
            lock (_gate)
            {
                var previous = LastHash();
                var entry = new AuditEntry
                {
                    Time = _clock().ToString("o"),
                    User = user ?? "unknown",
                    Action = action,
                    Resource = resource,
                    Outcome = outcome,
                    Detail = detail,
                    PreviousHash = previous
                };
                entry.Hash = Compute(entry);
                File.AppendAllText(_path, JsonSerializer.Serialize(entry) + "\n", new UTF8Encoding(false));
                return entry;
            }
        }

        /// <summary>Reads all entries.</summary>
        public IReadOnlyList<AuditEntry> ReadAll()
        {
            lock (_gate) return Load();
        }

        /// <summary>Returns true when every line links to the one before it and its own hash is correct.</summary>
        public bool Verify()
        {
            lock (_gate)
            {
                var previous = string.Empty;
                foreach (var e in Load())
                {
                    if ((e.PreviousHash ?? string.Empty) != previous) return false;
                    if (e.Hash != Compute(e)) return false;
                    previous = e.Hash;
                }
                return true;
            }
        }

        private List<AuditEntry> Load()
        {
            if (!File.Exists(_path)) return new List<AuditEntry>();
            return File.ReadAllLines(_path, Encoding.UTF8)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonSerializer.Deserialize<AuditEntry>(l))
                .ToList();
        }

        private string LastHash() => Load().LastOrDefault()?.Hash ?? string.Empty;

        private static string Compute(AuditEntry e)
        {
            var text = string.Join("\u001f", e.PreviousHash ?? string.Empty, e.Time, e.User, e.Action, e.Resource, e.Outcome, e.Detail);
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }
    }

    /// <summary>
    /// Masks patient data in X12 text so it can be put in logs, error reports and support tickets.
    /// Structure, segment IDs, codes, amounts and provider identifiers are kept; names, member and account numbers,
    /// addresses, phone numbers and dates of birth are replaced.
    /// </summary>
    public static class PhiRedactor
    {
        private const string Mask = "XXXX";

        private static readonly HashSet<string> RedactedReferences = new HashSet<string>(new[]
        {
            "SY", "1L", "1W", "IG", "EA", "F6", "6P", "HJ", "ZZ", "D9", "1K", "F8", "G1", "0K", "BB", "9A", "9C"
        });

        /// <summary>Returns the text with patient data masked. Unreadable text is returned as a single mask.</summary>
        public static string Redact(string x12)
        {
            if (string.IsNullOrEmpty(x12)) return x12;
            IReadOnlyList<X12Segment> segments;
            X12Delimiters d;
            try { segments = X12Tokenizer.Tokenize(x12, out d); }
            catch (FormatException) { return Mask; }

            var e = d.Element;
            var lines = new List<string>();
            foreach (var s in segments)
            {
                var elements = new List<string> { s.Id };
                for (var i = 1; i <= s.Count; i++) elements.Add(s[i]);

                switch (s.Id)
                {
                    case "NM1":
                        // Individuals (entity type 1): last, first, middle, suffix and the ID (except provider NPIs).
                        if (s[2] == "1" || s[1] == "IL" || s[1] == "QC")
                        {
                            Mask3(elements, 3, 4, 5, 7);
                            if (s[8] != "XX" && s[8] != "XV") MaskAt(elements, 9);
                        }
                        break;
                    case "N3": MaskAt(elements, 1); MaskAt(elements, 2); break;
                    case "N4": MaskAt(elements, 1); MaskAt(elements, 3); break;
                    case "DMG": if (s[2].Length >= 4) elements[2] = s[2].Substring(0, 4) + "0101"; break;
                    case "PER": MaskAt(elements, 2); MaskAt(elements, 4); MaskAt(elements, 6); MaskAt(elements, 8); break;
                    case "SBR": MaskAt(elements, 3); MaskAt(elements, 4); break;
                    case "CLM": MaskAt(elements, 1); break;
                    case "CLP": MaskAt(elements, 1); MaskAt(elements, 7); break;
                    case "TRN": MaskAt(elements, 2); break;
                    case "REF":
                        if (RedactedReferences.Contains(s[1]) || s[1] == "6R" || s[1] == "FJ") MaskAt(elements, 2);
                        break;
                    case "STC": MaskAt(elements, 12); break;
                    case "NTE":
                    case "MSG":
                    case "K3": MaskAt(elements, 1); MaskAt(elements, 2); break;
                }

                lines.Add(string.Join(e.ToString(), elements));
            }

            return string.Concat(lines.Select(l => l + d.Segment));
        }

        private static void Mask3(List<string> elements, params int[] positions)
        {
            foreach (var p in positions) MaskAt(elements, p);
        }

        private static void MaskAt(List<string> elements, int position)
        {
            if (position < elements.Count && !string.IsNullOrEmpty(elements[position])) elements[position] = Mask;
        }
    }

    /// <summary>
    /// Encrypts files at rest with AES-256-GCM. The format is: "EWP1", salt (16), nonce (12), tag (16), ciphertext.
    /// A password is turned into a key with PBKDF2-SHA256 (600000 iterations); a 32-byte key can be given directly
    /// (for example from a key vault). Tampering or a wrong key throws <see cref="CryptographicException"/>.
    /// </summary>
    public static class EdiFileProtector
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("EWP1");
        private const int SaltSize = 16;
        private const int NonceSize = 12;
        private const int TagSize = 16;
        private const int Iterations = 600000;

        /// <summary>Encrypts with a password.</summary>
        public static byte[] Encrypt(byte[] data, string password)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            RequirePassword(password);
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            return Seal(data, DeriveKey(password, salt), salt);
        }

        /// <summary>Decrypts a file made by <see cref="Encrypt(byte[], string)"/>.</summary>
        public static byte[] Decrypt(byte[] protectedData, string password)
        {
            RequirePassword(password);
            var salt = ReadSalt(protectedData);
            return Open(protectedData, DeriveKey(password, salt));
        }

        /// <summary>Encrypts with a 32-byte key.</summary>
        public static byte[] Encrypt(byte[] data, byte[] key)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            RequireKey(key);
            return Seal(data, key, new byte[SaltSize]);
        }

        /// <summary>Decrypts a file made by <see cref="Encrypt(byte[], byte[])"/>.</summary>
        public static byte[] Decrypt(byte[] protectedData, byte[] key)
        {
            RequireKey(key);
            ReadSalt(protectedData);
            return Open(protectedData, key);
        }

        private static byte[] Seal(byte[] data, byte[] key, byte[] salt)
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var cipher = new byte[data.Length];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(key, TagSize))
                aes.Encrypt(nonce, data, cipher, tag, Magic);

            var output = new byte[Magic.Length + SaltSize + NonceSize + TagSize + cipher.Length];
            var o = 0;
            Buffer.BlockCopy(Magic, 0, output, o, Magic.Length); o += Magic.Length;
            Buffer.BlockCopy(salt, 0, output, o, SaltSize); o += SaltSize;
            Buffer.BlockCopy(nonce, 0, output, o, NonceSize); o += NonceSize;
            Buffer.BlockCopy(tag, 0, output, o, TagSize); o += TagSize;
            Buffer.BlockCopy(cipher, 0, output, o, cipher.Length);
            return output;
        }

        private static byte[] Open(byte[] file, byte[] key)
        {
            var o = Magic.Length + SaltSize;
            var nonce = new byte[NonceSize];
            var tag = new byte[TagSize];
            Buffer.BlockCopy(file, o, nonce, 0, NonceSize); o += NonceSize;
            Buffer.BlockCopy(file, o, tag, 0, TagSize); o += TagSize;
            var cipher = new byte[file.Length - o];
            Buffer.BlockCopy(file, o, cipher, 0, cipher.Length);
            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(key, TagSize))
                aes.Decrypt(nonce, cipher, tag, plain, Magic);
            return plain;
        }

        private static byte[] ReadSalt(byte[] file)
        {
            if (file == null || file.Length < Magic.Length + SaltSize + NonceSize + TagSize)
                throw new CryptographicException("The data is not a protected file.");
            for (var i = 0; i < Magic.Length; i++)
                if (file[i] != Magic[i]) throw new CryptographicException("The data is not a protected file.");
            var salt = new byte[SaltSize];
            Buffer.BlockCopy(file, Magic.Length, salt, 0, SaltSize);
            return salt;
        }

        private static byte[] DeriveKey(string password, byte[] salt) =>
            Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);

        private static void RequirePassword(string password)
        {
            if (string.IsNullOrEmpty(password) || password.Length < 12)
                throw new ArgumentException("Use a password of at least 12 characters, or a 32-byte key from a key vault.", nameof(password));
        }

        private static void RequireKey(byte[] key)
        {
            if (key == null || key.Length != 32) throw new ArgumentException("The key must be 32 bytes.", nameof(key));
        }
    }
}
