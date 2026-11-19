# Audit source Fingering — 19 November 2026

Lingkup: source C# Core/App, alur vault/backup, protokol FK, tes simulator, dan packaging. Audit statis dan tes otomatis; tidak menghubungi mesin presensi kantor.

## Temuan yang diperbaiki

| Prioritas | Temuan | Perbaikan dan verifikasi |
|---|---|---|
| Tinggi | Sandi mesin tercetak di ringkasan tambah-user; hex trace login/enroll dapat mengungkap communication key, sandi, atau biometrik. | Hapus sandi dari ringkasan, sembunyikan TX/RX mentah untuk login dan enroll. Tes memeriksa trace dan hasil pekerjaan. |
| Tinggi | Kunci unik template hanya PIN+nomor backup sehingga mesin kedua bisa menimpa cadangan sidik jari mesin pertama. | Migrasi tabel menjadi PIN+mesin+nomor backup; verifikasi cadangan difilter per mesin. Tes mempertahankan data lama dan kedua mesin setelah membuka ulang database. |
| Sedang | Live monitoring mengabaikan communication key dan memakai Dictionary lintas task; pembatalan dapat melewatkan pelepasan semaphore. | Gunakan konfigurasi koneksi yang sama, ConcurrentDictionary, dan jalankan finally meskipun token dibatalkan. Tes live memakai communication key bukan nol. |
| Sedang | Nilai putaran PBKDF2 dan struktur vault dari file tidak divalidasi sebelum derivasi. | Batasi ukuran file/putaran/panjang payload, tolak file terpotong dan tambahan byte. Tes input rusak dan putaran ekstrem. |
| Sedang | Ukuran data/jumlah rekaman mesin tidak dibatasi sebelum alokasi memori. | Batas 64 MiB per transfer sesuai indeks blok 16-bit, 1 juta log dan 100 ribu user. Tes ukuran transfer negatif/ekstrem. |
| Sedang | Restart memakai shell dan kehilangan argumen --data. | Jalankan executable langsung dengan ArgumentList dan teruskan direktori data. |
| Rendah | README menyebut akun bawaan, lokasi DB lama, serta dokumentasi di folder induk yang tidak tersedia. | README baru menjelaskan setup terenkripsi, lokasi data, packaging dan batas bukti. |
| Sedang | Hanya .deb tersedia, dependensi runtime Linux belum lengkap; belum ada pipeline macOS/RPM. | Build native Linux DEB/RPM dan macOS Intel/ARM64, tes SQLCipher per host, verifikasi paket, DMG dan checksum. |

## Validasi

- Baseline Windows: 82 tes lulus sebelum perubahan.
- Tes regresi tambahan: vault rusak, kerahasiaan trace, batas transfer, migrasi cadangan lintas mesin. Hasil akhir dan validasi native dicatat di `BUILD-RESULTS.md` setelah pipeline selesai.
- Pemeriksaan advisory `dotnet list src/Fingering.App package --vulnerable --include-transitive --format json` tidak melaporkan paket rentan pada saat audit. Ini hanya berdasarkan advisory NuGet yang tersedia.
- Dependensi dan packaging mengacu pada [Avalonia Linux deployment](https://docs.avaloniaui.net/docs/deployment/linux), [Avalonia macOS deployment](https://docs.avaloniaui.net/docs/deployment/macos/), serta [runner GitHub](https://docs.github.com/en/actions/reference/runners/github-hosted-runners).

## Risiko dan batas yang masih ada

- Kompatibilitas perangkat asli, khususnya perintah tulis/hapus dan format enroll, membutuhkan pengujian pada perangkat uji dengan izin pemilik. Simulator tidak menggantikan itu.
- Pemindahan dua file database saat menerapkan restore belum menjadi transaksi filesystem tunggal; listrik mati/crash di tengah pemindahan memerlukan pemulihan dari folder `cadangan/sebelum-restore-*`. Ini memerlukan desain pemulihan crash tersendiri.
- Konfirmasi dan pencadangan sebelum hapus log tidak dapat membekukan mesin; scan baru yang masuk di sela backup dan perintah hapus berpotensi tidak tercadangkan. Jalankan hanya saat mesin tidak menerima presensi.
- `--password` untuk ekspor headless terlihat di argumen proses/shell history. Gunakan UI untuk ekspor jika perlu menjaga sandi dari pengguna lain pada host yang sama.
- DMG belum ditandatangani Developer ID/notarized. Paket RPM diverifikasi strukturnya pada Ubuntu; instalasi nyata di Fedora/RHEL dan GUI macOS di perangkat pengguna tetap perlu uji penerimaan.
- Source tidak menyertakan dokumen protokol/handover lama; klaim bukti perangkat terdahulu tidak dapat diverifikasi ulang dari salinan ini.
