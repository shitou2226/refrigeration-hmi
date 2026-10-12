using System.Text.Json;

namespace RefrigerationHmi;

public enum AcquisitionMode { Simulation, ModbusLoopback }
public enum DemoScenario { Normal, HighPressure, LinkLost }

public sealed record Settings
{
    public AcquisitionMode Mode { get; init; } = AcquisitionMode.Simulation;
    public int Port { get; init; } = 15020;
    public byte UnitId { get; init; } = 1;
    public int IntervalMs { get; init; } = 1000;
    public double HighTemperature { get; init; } = -15;
    public double HighPressure { get; init; } = 1.8;
    public void Validate()
    {
        if (Port is < 1024 or > 65535 || UnitId is < 1 or > 247 || IntervalMs is < 250 or > 10000)
            throw new ArgumentException("端口 1024–65535，站号 1–247，周期 250–10000 ms。");
        if (!double.IsFinite(HighTemperature) || HighTemperature is < -30 or > 10 ||
            !double.IsFinite(HighPressure) || HighPressure is < .5 or > 3)
            throw new ArgumentException("温度阈值范围 -30～10 °C，压力阈值 0.5～3 MPa。");
    }
}

public static class LocalFiles
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "AppData");
    public static string Database => Path.Combine(Root, "refrigeration.db");
    public static string Config => Path.Combine(Root, "settings.json");
    public static Settings LoadSettings()
    {
        Directory.CreateDirectory(Root);
        if (!File.Exists(Config)) return new();
        var result = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Config)) ?? new();
        result.Validate();
        return result;
    }
    public static void SaveSettings(Settings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(Root);
        File.WriteAllText(Config + ".tmp", JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(Config + ".tmp", Config, true);
    }
}

public sealed record Sample(DateTimeOffset Time, string Source, string Quality, double? Temperature,
    double? SuctionPressure, double? DischargePressure, double? Current, bool? Compressor,
    bool? Fan, double LatencyMs, string Error)
{
    public static Sample Failure(string source, string error, double latency) =>
        new(DateTimeOffset.UtcNow, source, "Bad", null, null, null, null, null, null, latency, error);
}
public sealed record AlarmEvent(long Id, string Code, string Name, string Severity, DateTimeOffset Raised,
    DateTimeOffset? Cleared, DateTimeOffset? Acknowledged, string Source);
public sealed record AlarmChange(string Code, string Name, string Severity, bool Active);

public sealed class AlarmEngine
{
    private readonly HashSet<string> active;
    public AlarmEngine(IEnumerable<string>? initial = null) => active = new(initial ?? []);
    public List<AlarmChange> Evaluate(Sample sample, Settings settings)
    {
        var changes = new List<AlarmChange>();
        Change("COMM", "采集通信中断", "故障", sample.Quality != "Good");
        // Invalid samples cannot prove that process alarms have recovered.
        if (sample.Quality == "Good")
        {
            var tempLimit = settings.HighTemperature - (active.Contains("TEMP") ? .5 : 0);
            var pressureLimit = settings.HighPressure - (active.Contains("PRESS") ? .05 : 0);
            Change("TEMP", "冷库温度偏高", "预警", sample.Temperature > tempLimit);
            Change("PRESS", "排气压力偏高", "故障", sample.DischargePressure > pressureLimit);
        }
        return changes;

        void Change(string code, string name, string severity, bool on)
        {
            if (on && active.Add(code)) changes.Add(new(code, name, severity, true));
            else if (!on && active.Remove(code)) changes.Add(new(code, name, severity, false));
        }
    }
}
