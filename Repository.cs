using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace RefrigerationHmi;

public sealed class Repository
{
    public string Path { get; }
    public Repository(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS samples (
              id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp_utc TEXT NOT NULL,
              source TEXT NOT NULL, quality TEXT NOT NULL CHECK(quality IN ('Good','Bad')),
              temperature_c REAL, suction_mpa REAL, discharge_mpa REAL, current_a REAL,
              compressor INTEGER, fan INTEGER, latency_ms REAL NOT NULL, error TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_samples_time ON samples(timestamp_utc);
            CREATE TABLE IF NOT EXISTS alarms (
              id INTEGER PRIMARY KEY AUTOINCREMENT, code TEXT NOT NULL, name TEXT NOT NULL,
              severity TEXT NOT NULL, raised_utc TEXT NOT NULL, cleared_utc TEXT,
              acknowledged_utc TEXT, source TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_alarm_active ON alarms(code) WHERE cleared_utc IS NULL;
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, DefaultTimeout = 3 }.ToString());
        db.Open();
        return db;
    }
    public static string Iso(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    public void Store(Sample s, IEnumerable<AlarmChange> changes)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO samples(timestamp_utc,source,quality,temperature_c,suction_mpa,discharge_mpa,current_a,compressor,fan,latency_ms,error)
            VALUES($time,$source,$quality,$temp,$suction,$discharge,$current,$compressor,$fan,$latency,$error);
            """;
        Bind(cmd, "$time", Iso(s.Time)); Bind(cmd, "$source", s.Source); Bind(cmd, "$quality", s.Quality);
        Bind(cmd, "$temp", s.Temperature); Bind(cmd, "$suction", s.SuctionPressure); Bind(cmd, "$discharge", s.DischargePressure);
        Bind(cmd, "$current", s.Current); Bind(cmd, "$compressor", s.Compressor); Bind(cmd, "$fan", s.Fan);
        Bind(cmd, "$latency", s.LatencyMs); Bind(cmd, "$error", s.Error);
        cmd.ExecuteNonQuery();
        foreach (var change in changes)
        {
            cmd.Parameters.Clear();
            cmd.CommandText = change.Active ? """
                INSERT INTO alarms(code,name,severity,raised_utc,source) VALUES($code,$name,$severity,$time,$source);
                """ : "UPDATE alarms SET cleared_utc=$time WHERE code=$code AND cleared_utc IS NULL;";
            Bind(cmd, "$code", change.Code); Bind(cmd, "$time", Iso(s.Time));
            if (change.Active) { Bind(cmd, "$name", change.Name); Bind(cmd, "$severity", change.Severity); Bind(cmd, "$source", s.Source); }
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
    private static void Bind(SqliteCommand cmd, string key, object? value) => cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
    public long Count()
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM samples;";
        return (long)cmd.ExecuteScalar()!;
    }
    public int ActiveCount() => ActiveCodes().Count;
    public List<string> ActiveCodes()
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT code FROM alarms WHERE cleared_utc IS NULL;";
        using var reader = cmd.ExecuteReader(); var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }
    public List<Sample> Query(DateTimeOffset start, DateTimeOffset end, string quality = "All", int limit = 5000, long beforeId = long.MaxValue)
        => QueryRows(start, end, quality, limit, beforeId).Select(r => r.Sample).ToList();
    private List<(long Id, Sample Sample)> QueryRows(DateTimeOffset start, DateTimeOffset end, string quality, int limit, long beforeId)
    {
        if (start > end) throw new ArgumentException("开始时间不能晚于结束时间。");
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id,timestamp_utc,source,quality,temperature_c,suction_mpa,discharge_mpa,current_a,compressor,fan,latency_ms,error
            FROM samples WHERE timestamp_utc >= $start AND timestamp_utc <= $end
              AND ($quality='All' OR quality=$quality) AND id < $before ORDER BY id DESC LIMIT $limit;
            """;
        Bind(cmd, "$start", Iso(start)); Bind(cmd, "$end", Iso(end)); Bind(cmd, "$quality", quality);
        Bind(cmd, "$before", beforeId); Bind(cmd, "$limit", limit);
        using var r = cmd.ExecuteReader(); var result = new List<(long, Sample)>();
        while (r.Read())
        {
            double? N(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
            bool? B(int i) => r.IsDBNull(i) ? null : r.GetBoolean(i);
            result.Add((r.GetInt64(0), new(DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture), r.GetString(2), r.GetString(3),
                N(4), N(5), N(6), N(7), B(8), B(9), r.GetDouble(10), r.GetString(11))));
        }
        return result;
    }
    public List<AlarmEvent> Alarms(bool activeOnly = false)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,code,name,severity,raised_utc,cleared_utc,acknowledged_utc,source FROM alarms " +
            (activeOnly ? "WHERE cleared_utc IS NULL " : "") + "ORDER BY id DESC LIMIT 2000;";
        using var r = cmd.ExecuteReader(); var result = new List<AlarmEvent>();
        while (r.Read())
        {
            DateTimeOffset? T(int i) => r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture);
            result.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), T(4)!.Value, T(5), T(6), r.GetString(7)));
        }
        return result;
    }
    public void Acknowledge(long id)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE alarms SET acknowledged_utc=$now WHERE id=$id AND acknowledged_utc IS NULL;";
        Bind(cmd, "$now", Iso(DateTimeOffset.UtcNow)); Bind(cmd, "$id", id); cmd.ExecuteNonQuery();
    }
    public int ExportCsv(string file, DateTimeOffset start, DateTimeOffset end, string quality)
    {
        // Snapshot the upper ID so continuous acquisition does not change this export.
        long before;
        using (var db = Open())
        using (var cmd = db.CreateCommand()) { cmd.CommandText = "SELECT COALESCE(MAX(id),0) FROM samples;"; before = (long)cmd.ExecuteScalar()! + 1; }
        using var output = new StreamWriter(file, false, new UTF8Encoding(true));
        output.WriteLine("UTC时间,数据来源,质量,冷库温度_C,吸气压力_MPa,排气压力_MPa,电流_A,压缩机,风机,延迟_ms,错误说明");
        int count = 0;
        while (true)
        {
            var rows = QueryRows(start, end, quality, 2000, before);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                var s = row.Sample;
                output.WriteLine(string.Join(',', new[] { Iso(s.Time), s.Source, s.Quality, Number(s.Temperature), Number(s.SuctionPressure),
                    Number(s.DischargePressure), Number(s.Current), s.Compressor?.ToString() ?? "", s.Fan?.ToString() ?? "",
                    Number(s.LatencyMs), s.Error }.Select(EscapeCsv)));
                count++;
            }
            before = rows[^1].Id;
        }
        return count;
    }
    private static string Number(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
    private static string EscapeCsv(string value)
    {
        // Avoid formula execution if a device-supplied error begins with a formula prefix.
        if (value.Length > 0 && "=+@".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
