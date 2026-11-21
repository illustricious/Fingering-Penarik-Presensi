# Hasil build dan validasi Fingering

## Release 1.1.0 — password user — 21 November 2026

[Release 1.1.0](https://github.com/illustricious/Fingering-Penarik-Presensi/releases/tag/v1.1.0) menggunakan source `f2b2b111482cd59dd4a7030d52a476370673bc98`. [Workflow build dan publikasi](https://github.com/illustricious/Fingering-Penarik-Presensi/actions/runs/44870797384) selesai **success** pada kelima runner dan job release.

| Runner native | Tes |
|---|---|
| Windows x64 | 99/99 lulus |
| Windows ARM64 | 99/99 lulus |
| Ubuntu Linux x64 | 99/99 lulus |
| Mac Intel | 99/99 lulus |
| Mac Apple Silicon | 99/99 lulus |

Penambahan tes mencakup penarikan password baca-saja, communication key, kerahasiaan log, hasil campuran/gagal/data lama, isolasi mesin, migrasi DB lama, enkripsi dan backup/restore, serta interaksi kontrol UI nyata pada platform headless Skia (navigasi tab, penyamaran/lihat/sembunyikan ulang, pencarian dan paginasi). [Render tabel](docs/password-user-table.png) menggunakan data fixture, bukan data kantor.

Keenam paket program, checksum dan laporan audit telah dipublikasikan sebagai delapan aset. Hash keenam paket dari manifest release cocok dengan digest SHA-256 GitHub. Format ASCII tetap disebut kandidat dan belum diverifikasi terhadap password asli perangkat. Tidak ada pengujian terhadap mesin kantor; DMG tetap ad-hoc dan EXE belum Authenticode-signed.

## Release v1.0.0, termasuk Windows ARM64

[Release yang dipublikasikan](https://github.com/illustricious/Fingering-Penarik-Presensi/releases/tag/v1.0.0) berasal dari commit `c805052dd77d62646b80991d02c5fc774d905207`, dibuild ulang melalui [workflow release](https://github.com/illustricious/Fingering-Penarik-Presensi/actions/runs/44442364522). Lima job native dan job publikasi release seluruhnya **success**.

| Runner native | Hasil tes | Verifikasi paket |
|---|---|---|
| Windows Server 2025 x64 | 88/88 lulus | EXE portable self-contained, header PE x64, executable --version |
| Windows 11 ARM64 | 88/88 lulus | SDK/host ARM64, SQLCipher ARM64, header PE ARM64, executable --version |
| Ubuntu 22.04 x64 | 88/88 lulus | DEB/RPM, payload RPM utuh, instalasi DEB dan GUI Xvfb |
| macOS 15 Intel | 88/88 lulus | DMG, codesign ad-hoc, mount dan executable --version |
| macOS 15 Apple Silicon | 88/88 lulus | DMG, codesign ad-hoc, mount dan executable --version |

Keenam paket program (Windows x64/ARM64, Linux DEB/RPM, Mac Intel/Apple Silicon), `SHA256SUMS.txt`, dan `AUDIT.md` sudah terunggah sebagai delapan aset release. Release bukan draft. Hash keenam paket dalam manifest telah dicocokkan dengan digest SHA-256 masing-masing aset dari API GitHub.

EXE Windows belum Authenticode-signed dan DMG belum Developer ID/notarized. Pengujian perangkat fisik dan instalasi RPM pada Fedora/RHEL tetap mengikuti batas audit sebelumnya.

## Build awal audit

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
