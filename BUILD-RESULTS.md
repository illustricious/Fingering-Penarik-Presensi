# Hasil build audit — 19 November 2026

Source aplikasi: commit `45dce2a4b574c1d84b4c89b4df1d669cc5bcf1a5`.

[Workflow native yang berhasil](https://github.com/illustricious/Fingering-Penarik-Presensi/actions/runs/44440764191). Ketiga job selesai dengan status **success**.

| Platform | Tes | Paket / verifikasi |
|---|---|---|
| Windows x64, SDK 10.0.404 lokal | 88 lulus | Build UI tanpa warning/error; publish self-contained dan `--version` menghasilkan 1.0.0. |
| Ubuntu 22.04 x64 | 88 lulus | DEB dibaca/dipasang dengan dpkg; RPM dibaca/diekstrak dengan rpm/rpm2cpio; isi executable RPM dibandingkan byte per byte dengan hasil publish. Executable paket menjalankan `--version`; GUI DEB bertahan sampai batas smoke test 10 detik melalui Xvfb. |
| macOS 15 Apple Silicon | 88 lulus | `.app` ditandatangani ad-hoc dan lolos codesign strict/deep. DMG lolos hdiutil verify, dipasang read-only, diverifikasi lagi, executable menjalankan `--version` dari volume DMG. |
| macOS 15 Intel | 88 lulus | Verifikasi codesign/DMG/mount/runtime yang sama dengan Apple Silicon. |

Paket versi 1.0.0:

- `fingering_1.0.0_amd64.deb`
- `fingering-1.0.0-1.x86_64.rpm`
- `Fingering-1.0.0-osx-arm64.dmg`
- `Fingering-1.0.0-osx-x64.dmg`
- `Fingering-1.0.0-win-x64.exe` (publish lokal sebagai tambahan)

Artifact GitHub mencakup checksum SHA-256 serta laporan tes TRX. Paket DMG belum Developer ID/notarized. RPM belum diuji instalasi pada distro Fedora/RHEL. Tes protokol memakai simulator, bukan perangkat Fingerspot asli; lihat [AUDIT.md](AUDIT.md) untuk batas audit dan risiko tersisa.
