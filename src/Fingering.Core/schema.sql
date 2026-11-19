-- Skema baru (POC) -- SQLite. Satu file database, tanpa server terpisah.
-- Waktu disimpan sebagai TEXT 'YYYY-MM-DD HH:MM:SS' (jam lokal mesin), tanggal 'YYYY-MM-DD'.
PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;

-- ===== Inti POC: tarik presensi dari mesin =====

CREATE TABLE device (
  id            INTEGER PRIMARY KEY,
  serial        TEXT NOT NULL UNIQUE,            -- device.sn lama
  name          TEXT NOT NULL DEFAULT '',
  protocol      TEXT NOT NULL DEFAULT 'fk'       -- driver: fk | zk | cloud | usb | unknown
                CHECK (protocol IN ('fk','zk','cloud','usb','unknown')),
  model         TEXT NOT NULL DEFAULT '',        -- mis. 'Revo W-230N'
  host          TEXT NOT NULL DEFAULT '',        -- IP / hostname
  port          INTEGER NOT NULL DEFAULT 5005,
  comm_key      TEXT NOT NULL DEFAULT '',        -- kata sandi komunikasi mesin
  machine_no    INTEGER NOT NULL DEFAULT 1,      -- device.dev_id lama
  enabled       INTEGER NOT NULL DEFAULT 1,
  transport     TEXT NOT NULL DEFAULT 'tcp' CHECK (transport IN ('tcp','udp')),
  layout        TEXT NOT NULL DEFAULT 'auto',    -- tata letak rekaman log: auto | B12 | A12 | B20 | C32 | G12 | G8
  pull_interval_min INTEGER NOT NULL DEFAULT 0,  -- 0 = manual saja
  last_error    TEXT NOT NULL DEFAULT '',
  last_pull_at  TEXT,                            -- terakhir tarik sukses
  last_cursor   TEXT NOT NULL DEFAULT '',        -- penanda lanjut (ATTLOGStamp lama / ID log terakhir)
  clock_offset_s INTEGER,                        -- selisih jam mesin vs server saat tarik terakhir
  created_at    TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE org_unit (                          -- gabungan pembagian1/2/3 lama
  id    INTEGER PRIMARY KEY,
  kind  TEXT NOT NULL CHECK (kind IN ('position','department','office')),
  name  TEXT NOT NULL,
  UNIQUE (kind, name)
);

CREATE TABLE employee (
  id             INTEGER PRIMARY KEY,
  pin            TEXT NOT NULL UNIQUE,           -- ID di mesin (pegawai_pin)
  nip            TEXT,
  name           TEXT NOT NULL,
  rfid           TEXT NOT NULL DEFAULT '',
  privilege      INTEGER NOT NULL DEFAULT 0,     -- 0 user,1 operator,2 sub admin,3 admin
  phone          TEXT,
  gender         INTEGER,                        -- 1 L, 2 P
  status         INTEGER NOT NULL DEFAULT 1,     -- 0 non aktif, 1 aktif, 2 berhenti
  position_id    INTEGER REFERENCES org_unit(id),
  department_id  INTEGER REFERENCES org_unit(id),
  office_id      INTEGER REFERENCES org_unit(id),
  hired_on       TEXT,
  resigned_on    TEXT,
  legacy_id      INTEGER,                        -- pegawai_id lama (untuk migrasi/cek silang)
  created_at     TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE punch (                             -- att_log lama
  id           INTEGER PRIMARY KEY,
  device_id    INTEGER NOT NULL REFERENCES device(id),
  pin          TEXT NOT NULL,                    -- sengaja TEXT bebas: log boleh ada sebelum pegawai terdaftar
  punched_at   TEXT NOT NULL,
  verify_mode  INTEGER NOT NULL DEFAULT 0,       -- jari/kartu/sandi/wajah (nilai mentah mesin)
  inout_mode   INTEGER NOT NULL DEFAULT 0,
  work_code    INTEGER NOT NULL DEFAULT 0,
  source       TEXT NOT NULL DEFAULT 'pull',     -- pull | realtime | webhook | import | manual
  sync_run_id  INTEGER REFERENCES sync_run(id),
  imported_at  TEXT NOT NULL DEFAULT (datetime('now')),
  UNIQUE (device_id, pin, punched_at)            -- kunci idempotensi: tarik ulang tidak menggandakan
);
CREATE INDEX punch_pin_time ON punch (pin, punched_at);
CREATE INDEX punch_time     ON punch (punched_at);

CREATE TABLE sync_run (                          -- riwayat tarik, untuk diagnosa
  id           INTEGER PRIMARY KEY,
  device_id    INTEGER NOT NULL REFERENCES device(id),
  started_at   TEXT NOT NULL,
  finished_at  TEXT,
  status       TEXT NOT NULL DEFAULT 'running' CHECK (status IN ('running','ok','error')),
  fetched      INTEGER NOT NULL DEFAULT 0,       -- baris diterima dari mesin
  inserted     INTEGER NOT NULL DEFAULT 0,       -- baris baru setelah dedup
  error        TEXT
);
CREATE INDEX sync_run_device ON sync_run (device_id, started_at);

CREATE TABLE app_user (
  id            INTEGER PRIMARY KEY,
  username      TEXT NOT NULL UNIQUE,
  password_hash TEXT NOT NULL,                   -- argon2id / bcrypt, BUKAN md5
  role          TEXT NOT NULL DEFAULT 'viewer' CHECK (role IN ('admin','operator','viewer')),
  created_at    TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE setting (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL DEFAULT ''
);

CREATE TABLE schema_version (version INTEGER NOT NULL);
INSERT INTO schema_version VALUES (1);

-- ===== Fase 2 (belum dipakai kantor; dibuat saat dibutuhkan, bukan di POC) =====
-- shift            <- jam_kerja        (bcin/cin/ecin/bcout/cout/ecout, toleransi, istirahat, min_countas)
-- schedule         <- jdw_kerja_m/d    (tipe normal | pola | auto)
-- schedule_assign  <- jdw_kerja_pegawai
-- holiday          <- libur
-- leave_type, leave <- jns_izin, izin, cuti_normatif, jatah_cuti
-- overtime, overtime_band <- lembur, index_ot
-- day_result       <- shift_result     (hasil hitung; aturan ada di RE-NOTES-hitung-absensi.md)
