using System.Text;
using Microsoft.Data.Sqlite;

namespace Fingering.Core;

public enum PasswordDataFormat { Unknown, Empty, NumericTextCandidate }
public sealed record PasswordAnalysis(PasswordDataFormat Format, string? Candidate = null);

/// <summary>Interpretasi konservatif rekaman CIF13_1. Kandidat ASCII bukan bukti password asli.</summary>
public static class MachinePassword
{
    public static PasswordAnalysis Analyze(string pin, ReadOnlySpan<byte> raw)
    {
        if (raw.Length != 84 || Layouts.IdText(raw[..32]) != pin || raw[32] != 10)
            return new(PasswordDataFormat.Unknown);
        var data = raw.Slice(52, 32);
        int end = data.IndexOf((byte)0);
        if (end < 0) return new(PasswordDataFormat.Unknown);
        foreach (byte value in data[end..])
            if (value != 0) return new(PasswordDataFormat.Unknown);
        if (end == 0) return new(PasswordDataFormat.Empty);
        if (end > 8) return new(PasswordDataFormat.Unknown);
        foreach (byte value in data[..end])
            if (value is < (byte)'0' or > (byte)'9') return new(PasswordDataFormat.Unknown);
        return new(PasswordDataFormat.NumericTextCandidate, Encoding.ASCII.GetString(data[..end]));
    }
}

public sealed record PasswordRead(string Pin, string Name, int Privilege, bool HasPassword, byte[]? Data, string Error = "");
public sealed record DevicePasswordRow(long DeviceId, string Device, string Pin, string Name, int Privilege,
    bool HasPassword, byte[]? Data, string FetchedAt, string AttemptedAt, string Error)
{
    public PasswordAnalysis Analysis => MachinePassword.Analyze(Pin, Data ?? []);
    public string Status => Error != "" ? (Data == null ? "Gagal dibaca" : "Gagal tarik; data lama") :
        !HasPassword ? "Tidak ada password" : Analysis.Format switch
        {
            PasswordDataFormat.NumericTextCandidate => "Kandidat teks; belum diverifikasi",
            PasswordDataFormat.Empty => "Data password kosong",
            _ => "Format belum dikenali",
        };
}

public sealed partial class Store
{
    const string PasswordColumns = "p.device_id,d.name,p.pin,p.name,p.privilege,p.has_password,p.raw_data,p.fetched_at,p.attempted_at,p.error";
    static DevicePasswordRow ReadPassword(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2),
        r.GetString(3), r.GetInt32(4), r.GetInt32(5) != 0, r.IsDBNull(6) ? null : (byte[])r[6], r.GetString(7), r.GetString(8), r.GetString(9));

    public (List<DevicePasswordRow> rows, int total) DevicePasswords(long deviceId, string search = "", int limit = 50, int offset = 0)
    {
        lock (gate)
        {
            const string where = "p.device_id=@d AND (@q='' OR p.pin=@q OR p.name LIKE '%'||@q||'%')";
            var total = (int)Scalar<long>($"SELECT count(*) FROM device_password p WHERE {where}", ("@d", deviceId), ("@q", search));
            var rows = Query($@"SELECT {PasswordColumns} FROM device_password p JOIN device d ON d.id=p.device_id
                WHERE {where} ORDER BY CAST(p.pin AS INTEGER),p.pin LIMIT @lim OFFSET @off", ReadPassword,
                ("@d", deviceId), ("@q", search), ("@lim", Math.Clamp(limit, 1, 200)), ("@off", Math.Max(0, offset)));
            return (rows, total);
        }
    }

    /// <summary>Ganti snapshot mesin secara atomik. Gagal baca mempertahankan data sukses sebelumnya dengan penanda data lama.</summary>
    public void SavePasswordSnapshot(long deviceId, IReadOnlyList<PasswordRead> reads)
    {
        if (reads.Select(r => r.Pin).Distinct(StringComparer.Ordinal).Count() != reads.Count)
            throw new InvalidDataException("daftar PIN mesin berisi duplikat");
        foreach (var read in reads)
            if (read.Error == "" && read.HasPassword && read.Data?.Length != 84)
                throw new InvalidDataException("rekaman password harus 84 byte");
        lock (gate)
        {
            var previous = Query($"SELECT {PasswordColumns} FROM device_password p JOIN device d ON d.id=p.device_id WHERE p.device_id=@d",
                ReadPassword, ("@d", deviceId)).ToDictionary(r => r.Pin, StringComparer.Ordinal);
            var now = DateTime.Now.ToString(TimeFmt, System.Globalization.CultureInfo.InvariantCulture);
            using var tx = db.BeginTransaction();
            using var delete = db.CreateCommand();
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM device_password WHERE device_id=@d";
            delete.Parameters.AddWithValue("@d", deviceId);
            delete.ExecuteNonQuery();
            foreach (var read in reads)
            {
                previous.TryGetValue(read.Pin, out var old);
                bool failed = read.Error != "";
                byte[]? data = !read.HasPassword ? null : failed ? old?.Data : read.Data;
                string fetched = failed ? old?.FetchedAt ?? "" : now;
                using var insert = db.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = @"INSERT INTO device_password(device_id,pin,name,privilege,has_password,raw_data,fetched_at,attempted_at,error)
                    VALUES(@d,@p,@n,@pr,@h,@raw,@f,@a,@e)";
                insert.Parameters.AddWithValue("@d", deviceId); insert.Parameters.AddWithValue("@p", read.Pin);
                insert.Parameters.AddWithValue("@n", read.Name == "" ? old?.Name ?? "" : read.Name); insert.Parameters.AddWithValue("@pr", read.Privilege);
                insert.Parameters.AddWithValue("@h", read.HasPassword ? 1 : 0);
                insert.Parameters.AddWithValue("@raw", (object?)data ?? DBNull.Value);
                insert.Parameters.AddWithValue("@f", fetched); insert.Parameters.AddWithValue("@a", now);
                insert.Parameters.AddWithValue("@e", read.Error); insert.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
}
