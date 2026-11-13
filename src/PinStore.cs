using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeCurtain
{
    /// <summary>
    /// Salted PBKDF2-HMAC-SHA256 hash of the unlock PIN. The PIN itself is never written anywhere.
    /// File format, one line:  v1$pbkdf2-sha256$iterations$salt(base64)$hash(base64)
    ///
    /// This keeps the PIN out of plain text. It is not strong protection for a short numeric PIN:
    /// anyone who can read the file can also just close the curtain process.
    /// </summary>
    internal sealed class PinStore
    {
        public const int MinLength = 4;
        public const int MaxLength = 12;
        public const int DefaultIterations = 40000;

        private const string Version = "v1";
        private const string Kdf = "pbkdf2-sha256";

        private readonly int iterations;
        private readonly byte[] salt;
        private readonly byte[] hash;

        private PinStore(int iterations, byte[] salt, byte[] hash)
        {
            this.iterations = iterations;
            this.salt = salt;
            this.hash = hash;
        }

        public static bool IsValidPin(string pin, out string why)
        {
            why = null;
            if (pin == null || pin.Length < MinLength)
            {
                why = "Use at least " + MinLength + " digits.";
                return false;
            }
            if (pin.Length > MaxLength)
            {
                why = "Use at most " + MaxLength + " digits.";
                return false;
            }
            for (int i = 0; i < pin.Length; i++)
            {
                if (pin[i] < '0' || pin[i] > '9')
                {
                    why = "Digits only (0-9).";
                    return false;
                }
            }
            return true;
        }

        /// <summary>Hashes the PIN with a fresh random salt, writes the file, then reads it back to prove it verifies.</summary>
        public static void Save(string path, string pin)
        {
            string why;
            if (!IsValidPin(pin, out why)) throw new ArgumentException(why);

            byte[] newSalt = new byte[16];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(newSalt);
            }
            byte[] newHash = Derive(pin, newSalt, DefaultIterations);
            string line = Version + "$" + Kdf + "$" + DefaultIterations + "$" +
                          Convert.ToBase64String(newSalt) + "$" + Convert.ToBase64String(newHash);

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, line + Environment.NewLine, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);

            PinStore check = Load(path);
            if (!check.Verify(pin)) throw new IOException("The PIN file failed its read-back check.");
        }

        public static PinStore Load(string path)
        {
            string text = File.ReadAllText(path).Trim();
            string[] parts = text.Split('$');
            if (parts.Length != 5 || parts[0] != Version || parts[1] != Kdf)
                throw new FormatException("Unrecognised PIN file format.");
            int iter;
            if (!int.TryParse(parts[2], out iter) || iter < 1000 || iter > 5000000)
                throw new FormatException("Bad iteration count in PIN file.");
            byte[] fileSalt = Convert.FromBase64String(parts[3]);
            byte[] fileHash = Convert.FromBase64String(parts[4]);
            if (fileSalt.Length < 8 || fileHash.Length != 32)
                throw new FormatException("Bad salt or hash length in PIN file.");
            return new PinStore(iter, fileSalt, fileHash);
        }

        public bool Verify(string pin)
        {
            if (pin == null) return false;
            byte[] candidate = Derive(pin, salt, iterations);
            int diff = candidate.Length ^ hash.Length;
            for (int i = 0; i < candidate.Length && i < hash.Length; i++)
            {
                diff |= candidate[i] ^ hash[i];
            }
            return diff == 0;
        }

        private static byte[] Derive(string pin, byte[] useSalt, int useIterations)
        {
            using (Rfc2898DeriveBytes kdf = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(pin), useSalt, useIterations, HashAlgorithmName.SHA256))
            {
                return kdf.GetBytes(32);
            }
        }
    }
}
