# CLAUDE.md — Fingering (untuk agen AI)

Mulai dari `README.md` dan `AUDIT.md`. Dokumen HANDOVER/PROTOKOL-FK yang disebut pada komentar lama tidak disertakan dalam salinan ini. Folder ini adalah root repository; hasil build native berada di `artifacts/packages`.

## Perintah
```powershell
# Gunakan .NET SDK 10 dari PATH.
dotnet build src/Fingering.App -c Release
dotnet test tests/Fingering.Tests -c Release
powershell -ExecutionPolicy Bypass -File publish.ps1  # tes + publish -> ..\Fingering.exe (tutup aplikasi dulu)
Fingering.exe --data <folder> --export 2026-10-16 2026-10-25 out.xlsx   # ekspor tanpa jendela
```
Path folder mengandung spasi dan `!!`: selalu beri tanda kutip. Jangan `dotnet publish -o ..` (galat CS5001); pakai `publish.ps1`.

## Tata letak
`src/Fingering.Core` = logika (protokol FK, SQLite, rekap/denda, XLSX), tanpa UI. `src/Fingering.App` = Avalonia (UI dibuat dari kode C#; gaya di `App.axaml` dan `Ui/Neo.cs`). `tests/Fingering.Tests` = xUnit + `Simulator.cs` (mesin tiruan).

## Aturan
- Jangan kirim perintah tulis (hapus log, tambah/hapus user, atur jam) ke mesin kantor tanpa izin eksplisit pengguna; uji dengan `Simulator`. Fitur tulis default mati dan harus tetap berpengaman (cadangkan + verifikasi dulu, konfirmasi ganda).
- Tambah perintah protokol baru di klien -> tambahkan juga ke `Simulator.cs` dan tes.
- Skema DB: ubah `src/Fingering.Core/schema.sql` ATAU migrasi di konstruktor `Store` (kolom tambahan seperti `employee.shifts` dibuat lewat ALTER); sinkronkan `..\schema.sql` + `..\migrate_backup.py` bila perlu.
- Jujur soal bukti: tandai TERBUKTI / TAFSIRAN / SIMULATOR; jangan mengklaim sesuatu "jalan di mesin" bila hanya lulus simulator.
- Bahasa UI dan komunikasi: Indonesia santai; teks UI boleh berhumor ("bro"); gaya neobrutalism; tanpa web wrapper.
