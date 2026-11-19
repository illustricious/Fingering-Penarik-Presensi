// Brankas kunci (vault.fkv): menyimpan kunci data 256-bit acak yang dipakai membuka database terenkripsi (SQLCipher).
// Kunci data TIDAK pernah disimpan polos; ia dibungkus dua kali dengan AES-256-GCM:
//   1) kunci turunan dari sandi login (PBKDF2-HMAC-SHA256, 600.000 putaran, salt acak) - payload: ID pengguna + kunci data
//   2) kunci turunan dari kode pemulihan (160 bit acak, dicetak sekali saat setup)        - payload: kunci data
// Akibatnya ganti sandi hanya membungkus ulang kunci (tanpa mengenkripsi ulang DB) dan lupa sandi masih bisa dipulihkan dengan kode pemulihan.
// Format berkas buatan sendiri (magic "FKV1"); primitif kripto memakai pustaka standar .NET, bukan algoritma buatan sendiri.
using System.Security.Cryptography;
using System.Text;

namespace Fingering.Core;

public static class Vault
{
    public const string FileName = "vault.fkv";
    const int KeyLen = 32, SaltLen = 16, NonceLen = 12, TagLen = 16, PwIterations = 600_000, RecIterations = 100_000;
    static readonly byte[] Magic = "FKV1"u8.ToArray();
    const byte Version = 1;

    sealed class Wrap
    {
        public int Iterations, Index;
        public byte[] Salt = [], Nonce = [], Cipher = [];   // Cipher = ciphertext + tag
    }

    public static string PathIn(string dir) => Path.Combine(dir, FileName);
    public static bool Exists(string dir) => File.Exists(PathIn(dir));

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyLen);

    // ---- publik ----

    /// <summary>Buat brankas baru: kunci data acak + kode pemulihan. Mengembalikan kunci (untuk membuka/membuat DB) dan kode pemulihan (tampilkan SEKALI).</summary>
    public static (byte[] key, string recovery) Create(string dir, string user, string password, byte[]? existingKey = null)
    {
        CheckCredentials(user, password);
        var key = existingKey ?? NewKey();
        var code = NewCode();
        Directory.CreateDirectory(dir);
        Save(dir, WrapPassword(key, user, password), WrapRecovery(key, code));
        return (key, code);
    }

    /// <summary>Buka dengan ID + sandi. null bila salah (ID dan sandi sama-sama diperiksa lewat autentikasi AES-GCM).</summary>
    public static byte[]? Unlock(string dir, string user, string password)
    {
        var (pw, _) = Load(dir);
        var kek = Kdf(password, pw.Salt, pw.Iterations);
        try
        {
            var plain = Open(kek, pw);
            if (plain == null) return null;
            try
            {
                int n = plain[0];
                if (plain.Length != 1 + n + KeyLen) return null;
                var u = Encoding.UTF8.GetString(plain, 1, n);
                // perbandingan waktu-tetap atas ID, supaya ID yang salah tidak bisa dibedakan dari sandi yang salah
                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(u), Encoding.UTF8.GetBytes(user.Trim()))) return null;
                return plain[(1 + n)..];
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    /// <summary>Buka dengan kode pemulihan (dipakai saat lupa sandi). null bila salah.</summary>
    public static byte[]? UnlockRecovery(string dir, string code)
    {
        var (_, rec) = Load(dir);
        var kek = Kdf(Normalize(code), rec.Salt, rec.Iterations);
        try { return Open(kek, rec); }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    /// <summary>Ganti ID/sandi: bungkus ulang kunci data (kode pemulihan tetap berlaku). DB tidak disentuh.</summary>
    public static void ChangeLogin(string dir, byte[] key, string user, string password)
    {
        CheckCredentials(user, password);
        var (_, rec) = Load(dir);
        Save(dir, WrapPassword(key, user, password), rec);
    }

    /// <summary>Buat kode pemulihan baru (yang lama tidak berlaku lagi). Mengembalikan kode barunya.</summary>
    public static string NewRecoveryCode(string dir, byte[] key)
    {
        var (pw, _) = Load(dir);
        var code = NewCode();
        Save(dir, pw, WrapRecovery(key, code));
        return code;
    }

    public static void CheckCredentials(string user, string password)
    {
        if (user.Trim() == "") throw new InvalidOperationException("ID wajib diisi");
        if (Encoding.UTF8.GetByteCount(user.Trim()) > 100) throw new InvalidOperationException("ID terlalu panjang");
        if (password.Length < 8) throw new InvalidOperationException("sandi minimal 8 karakter");
    }

    // ---- kode pemulihan ----
    const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string NewCode()
    {
        var b = RandomNumberGenerator.GetBytes(20);   // 160 bit
        var sb = new StringBuilder();
        int bits = 0, acc = 0;
        foreach (var x in b)
        {
            acc = acc << 8 | x; bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[acc >> (bits - 5) & 31]); bits -= 5; }
        }
        var raw = sb.ToString();   // 32 karakter
        return string.Join('-', Enumerable.Range(0, 8).Select(i => raw.Substring(i * 4, 4)));
    }

    /// <summary>Huruf besar, buang spasi/strip; 0->O 1->I 8->B (karakter yang sering salah baca) tidak ada di alfabet.</summary>
    public static string Normalize(string code)
    {
        var sb = new StringBuilder();
        foreach (var c in code.ToUpperInvariant())
            if (char.IsLetterOrDigit(c)) sb.Append(c switch { '0' => 'O', '1' => 'I', '8' => 'B', _ => c });
        return sb.ToString();
    }

    // ---- internal ----
    static byte[] Kdf(string secret, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, iterations, HashAlgorithmName.SHA256, KeyLen);

    static byte[] Aad(int index) => [.. Magic, Version, (byte)index];

    static Wrap Seal(byte[] secretKek, byte[] salt, int iterations, byte[] plain, int index)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var ct = new byte[plain.Length];
        var tag = new byte[TagLen];
        using (var gcm = new AesGcm(secretKek, TagLen)) gcm.Encrypt(nonce, plain, ct, tag, Aad(index));
        return new Wrap { Iterations = iterations, Index = index, Salt = salt, Nonce = nonce, Cipher = [.. ct, .. tag] };
    }

    static byte[]? Open(byte[] kek, Wrap w)
    {
        int index = w.Index;
        var ct = w.Cipher[..^TagLen]; var tag = w.Cipher[^TagLen..];
        var plain = new byte[ct.Length];
        try { using var gcm = new AesGcm(kek, TagLen); gcm.Decrypt(w.Nonce, ct, tag, plain, Aad(index)); }
        catch (CryptographicException) { return null; }
        return plain;
    }


    static Wrap WrapPassword(byte[] key, string user, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLen);
        var kek = Kdf(password, salt, PwIterations);
        var u = Encoding.UTF8.GetBytes(user.Trim());
        var plain = new byte[] { (byte)u.Length }.Concat(u).Concat(key).ToArray();
        try { return Seal(kek, salt, PwIterations, plain, 0); }
        finally { CryptographicOperations.ZeroMemory(kek); CryptographicOperations.ZeroMemory(plain); }
    }

    static Wrap WrapRecovery(byte[] key, string code)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLen);
        var kek = Kdf(Normalize(code), salt, RecIterations);
        try { return Seal(kek, salt, RecIterations, key, 1); }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    static void Save(string dir, Wrap pw, Wrap rec)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            w.Write(Magic); w.Write(Version);
            foreach (var x in new[] { pw, rec })
            {
                w.Write(x.Iterations); w.Write(x.Salt); w.Write(x.Nonce); w.Write((ushort)x.Cipher.Length); w.Write(x.Cipher);
            }
        }
        var path = PathIn(dir);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, ms.ToArray());
        File.Move(tmp, path, overwrite: true);
    }

    static (Wrap pw, Wrap rec) Load(string dir)
    {
        var path = PathIn(dir);
        if (new FileInfo(path).Length > 4096) throw new InvalidDataException("vault.fkv terlalu besar");
        var b = File.ReadAllBytes(path);
        using var r = new BinaryReader(new MemoryStream(b));
        if (b.Length < 5 || !r.ReadBytes(4).AsSpan().SequenceEqual(Magic) || r.ReadByte() != Version) throw new InvalidDataException("vault.fkv rusak atau versi tidak dikenal");
        Wrap Read(int index)
        {
            try
            {
                int iterations = r.ReadInt32();
                if (iterations is < 100_000 or > 1_000_000) throw new InvalidDataException("putaran KDF vault tidak valid");
                var salt = r.ReadBytes(SaltLen); var nonce = r.ReadBytes(NonceLen);
                int length = r.ReadUInt16();
                if (salt.Length != SaltLen || nonce.Length != NonceLen ||
                    (index == 0 ? length is < 50 or > 149 : length != KeyLen + TagLen))
                    throw new InvalidDataException("struktur vault.fkv rusak");
                var cipher = r.ReadBytes(length);
                if (cipher.Length != length) throw new InvalidDataException("vault.fkv terpotong");
                return new Wrap { Iterations = iterations, Index = index, Salt = salt, Nonce = nonce, Cipher = cipher };
            }
            catch (EndOfStreamException e) { throw new InvalidDataException("vault.fkv terpotong", e); }
        }
        var pw = Read(0); var rec = Read(1);
        if (r.BaseStream.Position != b.Length) throw new InvalidDataException("data tambahan di vault.fkv");
        return (pw, rec);
    }
}
