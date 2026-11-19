Fingering adalah rewrite aplikasi desktop penarik presensi Fingerspot Revo menggunakan Avalonia dan .NET 10.

Pilih paket sesuai OS dan arsitektur:

| Platform | File |
|---|---|
| Windows Intel/AMD 64-bit | `Fingering-1.0.0-win-x64.exe` |
| Windows ARM64 (mis. Snapdragon) | `Fingering-1.0.0-win-arm64.exe` |
| Ubuntu/Debian x64 | `fingering_1.0.0_amd64.deb` |
| Fedora/RHEL x64 | `fingering-1.0.0-1.x86_64.rpm` |
| Mac Intel | `Fingering-1.0.0-osx-x64.dmg` |
| Mac Apple Silicon | `Fingering-1.0.0-osx-arm64.dmg` |

Runtime .NET disertakan. EXE Windows bersifat portable; jalankan langsung. Pada macOS, buka DMG lalu seret Fingering ke Applications. Saat pertama dibuka, buat ID/sandi sendiri dan simpan kode pemulihan.

Build diuji pada runner native Windows x64/ARM64, Linux x64, dan Mac Intel/Apple Silicon, termasuk tes SQLCipher dan simulator protokol FK. DEB diuji instalasi dan pembukaan GUI melalui Xvfb; RPM diverifikasi payload dan executable; DMG diverifikasi signatures, checksum, dan executable dari mounted image.

Perbaikan audit mencakup penyembunyian sandi/communication key/data enroll dari log, cadangan sidik jari per mesin, monitoring dengan communication key, validasi vault/ukuran transfer, dan restart yang mempertahankan folder data. Lihat `AUDIT.md` untuk detail dan risiko tersisa. `SHA256SUMS.txt` memuat checksum keenam paket.

DMG menggunakan signing ad-hoc, belum Developer ID/notarized Apple. EXE Windows belum ditandatangani Authenticode. Tes protokol memakai simulator dan belum menguji perangkat Fingerspot fisik. Instalasi RPM pada Fedora/RHEL masih perlu uji penerimaan.
