// Pembuat paket .deb tanpa dpkg (bisa dijalankan di Windows): MakeDeb <folder-publish-linux-x64> <versi> <hasil.deb> <icon.png>
// Format .deb = arsip "ar" berisi: debian-binary, control.tar.gz, data.tar.gz. Berkas dipasang root:root dengan mode yang benar.
// Hasil: /opt/illustricious/fingering/Fingering (satu berkas), /usr/bin/fingering (symlink), menu aplikasi (.desktop) + ikon.
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

if (args.Length < 4) { Console.Error.WriteLine("pakai: MakeDeb <publishDir> <versi> <hasil.deb> <icon.png>"); return 2; }
var (pubDir, version, outDeb, icon) = (args[0], args[1], args[2], args[3]);
var exe = Path.Combine(pubDir, "Fingering");
if (!File.Exists(exe)) { Console.Error.WriteLine("tidak ada " + exe + " (publish linux-x64 dulu)"); return 1; }
var now = DateTimeOffset.UtcNow;

const string InstallDir = "opt/illustricious/fingering";
var desktop = string.Join('\n', "[Desktop Entry]", "Type=Application", "Name=Fingering", "GenericName=Penarik presensi",
    "Comment=Rewrite aplikasi penarik presensi Fingerspot Revo", "Exec=/usr/bin/fingering", "Icon=fingering", "Terminal=false",
    "Categories=Office;Utility;", "Keywords=presensi;absensi;fingerprint;") + "\n";
var copyright = "Fingering\nCopyright (c) illustricious. Seluruh hak dilindungi.\n";

// ---- isi berkas (path tanpa "./", mode, isi) ----
var files = new List<(string path, UnixFileMode mode, byte[] data)>
{
    ($"{InstallDir}/Fingering", (UnixFileMode)0b111_101_101, File.ReadAllBytes(exe)),                    // 0755
    ("usr/share/applications/fingering.desktop", (UnixFileMode)0b110_100_100, Encoding.UTF8.GetBytes(desktop)),   // 0644
    ("usr/share/pixmaps/fingering.png", (UnixFileMode)0b110_100_100, File.ReadAllBytes(icon)),
    ("usr/share/doc/fingering/copyright", (UnixFileMode)0b110_100_100, Encoding.UTF8.GetBytes(copyright)),
};
var dirs = new[] { "", "opt", "opt/illustricious", InstallDir, "usr", "usr/bin", "usr/share", "usr/share/applications", "usr/share/pixmaps", "usr/share/doc", "usr/share/doc/fingering" };

byte[] Tar(Action<TarWriter> fill)
{
    using var ms = new MemoryStream();
    using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, true))
    using (var tw = new TarWriter(gz, TarEntryFormat.Ustar, true)) fill(tw);
    return ms.ToArray();
}

void Own(UstarTarEntry e, UnixFileMode mode) { e.Mode = mode; e.Uid = 0; e.Gid = 0; e.UserName = "root"; e.GroupName = "root"; e.ModificationTime = now; }

var dataTgz = Tar(tw =>
{
    foreach (var d in dirs)
    {
        var e = new UstarTarEntry(TarEntryType.Directory, d == "" ? "./" : "./" + d + "/"); Own(e, (UnixFileMode)0b111_101_101); tw.WriteEntry(e);
    }
    foreach (var (path, mode, data) in files)
    {
        var e = new UstarTarEntry(TarEntryType.RegularFile, "./" + path) { DataStream = new MemoryStream(data) }; Own(e, mode); tw.WriteEntry(e);
    }
    var link = new UstarTarEntry(TarEntryType.SymbolicLink, "./usr/bin/fingering") { LinkName = "/" + InstallDir + "/Fingering" };
    Own(link, (UnixFileMode)0b111_111_111); tw.WriteEntry(link);
});

var installedKb = (files.Sum(f => (long)f.data.Length) + 1023) / 1024;
var control = string.Join('\n',
    "Package: fingering", $"Version: {version}", "Section: utils", "Priority: optional", "Architecture: amd64",
    $"Installed-Size: {installedKb}",
    "Maintainer: illustricious",
    "Depends: libc6 (>= 2.35), libstdc++6, libgcc-s1, zlib1g, libssl3 | libssl3t64, libx11-6, libice6, libsm6, libfontconfig1, libfreetype6",
    "Recommends: libxcursor1, libxrandr2, libxi6, libgl1",
    "Description: Penarik presensi mesin Fingerspot Revo (protokol FK)",
    " Aplikasi desktop untuk menarik log presensi dari mesin sidik jari, menghitung",
    " keterlambatan/lupa absen, dan mengekspor rekap XLSX. Database terenkripsi",
    " (AES-256) di ~/.config/illustricious/Fingering. Dikembangkan oleh illustricious.") + "\n";
var md5 = string.Concat(files.Select(f => Convert.ToHexString(MD5.HashData(f.data)).ToLowerInvariant() + "  " + f.path + "\n"));
var post = "#!/bin/sh\nset -e\nif command -v update-desktop-database >/dev/null 2>&1; then update-desktop-database -q /usr/share/applications || true; fi\nexit 0\n";

var controlTgz = Tar(tw =>
{
    foreach (var (name, text, mode) in new[] { ("control", control, 0b110_100_100), ("md5sums", md5, 0b110_100_100), ("postinst", post, 0b111_101_101), ("postrm", post, 0b111_101_101) })
    {
        var e = new UstarTarEntry(TarEntryType.RegularFile, "./" + name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) }; Own(e, (UnixFileMode)mode); tw.WriteEntry(e);
    }
});

// ---- arsip ar ----
using (var o = File.Create(outDeb))
{
    o.Write("!<arch>\n"u8);
    void Member(string name, byte[] data)
    {
        var h = $"{name + "/",-16}{DateTimeOffset.UtcNow.ToUnixTimeSeconds(),-12}{0,-6}{0,-6}{"100644",-8}{data.Length,-10}`\n";
        o.Write(Encoding.ASCII.GetBytes(h)); o.Write(data);
        if (data.Length % 2 == 1) o.WriteByte((byte)'\n');
    }
    Member("debian-binary", "2.0\n"u8.ToArray());
    Member("control.tar.gz", controlTgz);
    Member("data.tar.gz", dataTgz);
}

// ---- verifikasi: baca ulang arsip dan tampilkan isinya ----
Console.WriteLine($"{outDeb}  ({new FileInfo(outDeb).Length / 1024.0 / 1024.0:N1} MB)");
var bytes = File.ReadAllBytes(outDeb);
int p = 8;
if (Encoding.ASCII.GetString(bytes, 0, 8) != "!<arch>\n") { Console.Error.WriteLine("header ar rusak"); return 1; }
while (p < bytes.Length)
{
    var hdr = Encoding.ASCII.GetString(bytes, p, 60);
    if (!hdr.EndsWith("`\n")) { Console.Error.WriteLine("header anggota ar rusak"); return 1; }
    var name = hdr[..16].Trim().TrimEnd('/'); var size = int.Parse(hdr.Substring(48, 10).Trim());
    p += 60;
    Console.WriteLine($"  ar: {name} ({size} byte)");
    if (name.EndsWith(".tar.gz"))
    {
        using var gz = new GZipStream(new MemoryStream(bytes, p, size), CompressionMode.Decompress);
        using var tr = new TarReader(gz);
        while (tr.GetNextEntry() is { } e)
            Console.WriteLine($"      {Convert.ToString((int)e.Mode, 8),4} {(e as PosixTarEntry)?.UserName}/{(e as PosixTarEntry)?.GroupName} {e.Length,10} {e.Name}{(e.EntryType == TarEntryType.SymbolicLink ? " -> " + e.LinkName : "")}");
    }
    p += size + (size % 2);
}
return 0;
