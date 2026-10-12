using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace RefrigerationHmi;

public interface IDataSource : IDisposable
{
    string Name { get; }
    Task<Sample> ReadAsync(CancellationToken token);
}

public sealed class SimulationSource(Func<DemoScenario> scenario) : IDataSource
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    public string Name => "SIM · 演示数据";
    public Task<Sample> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (scenario() == DemoScenario.LinkLost) throw new IOException("模拟通信中断（故障演示）");
        return Task.FromResult(Create(clock.Elapsed.TotalSeconds, scenario(), Name));
    }
    public static Sample Create(double t, DemoScenario scenario, string source)
    {
        var high = scenario == DemoScenario.HighPressure;
        return new(DateTimeOffset.UtcNow, source, "Good", -18 + .65 * Math.Sin(t / 13) + .1 * Math.Sin(t * 1.4),
            .28 + .018 * Math.Sin(t / 9), high ? 2.12 + .04 * Math.Sin(t) : 1.32 + .07 * Math.Sin(t / 11),
            high ? 31.2 : 23.6 + 1.4 * Math.Sin(t / 8), true, true, .4, "");
    }
    public void Dispose() { }
}

// Demo mapping: FC03, zero-based register 0..5; all 16-bit, temperature signed.
public sealed class ModbusSource(Settings settings) : IDataSource
{
    private ushort transaction;
    public string Name => $"TCP · 127.0.0.1:{settings.Port}";
    public async Task<Sample> ReadAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(1500);
        var ct = timeout.Token;
        using var client = new TcpClient();
        var watch = Stopwatch.StartNew();
        await client.ConnectAsync(IPAddress.Loopback, settings.Port, ct);
        using var stream = client.GetStream();
        var request = new byte[12];
        var id = ++transaction;
        BinaryPrimitives.WriteUInt16BigEndian(request, id);
        request[5] = 6;
        request[6] = settings.UnitId;
        request[7] = 3;
        request[11] = 6;
        await stream.WriteAsync(request, ct);
        var header = new byte[7];
        await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        if (BinaryPrimitives.ReadUInt16BigEndian(header) != id || header[2] != 0 || header[3] != 0 ||
            header[6] != settings.UnitId || length is < 3 or > 254)
            throw new IOException("Modbus 响应头校验失败");
        var body = new byte[length - 1];
        await stream.ReadExactlyAsync(body, ct);
        if (body[0] == 0x83) throw new IOException($"Modbus 异常码 {body[1]}");
        if (body[0] != 3 || body[1] != 12 || body.Length != 14) throw new IOException("Modbus 点表长度不匹配");
        short temp = BinaryPrimitives.ReadInt16BigEndian(body.AsSpan(2));
        double suction = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(4)) / 1000d;
        double discharge = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(6)) / 1000d;
        double current = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(8)) / 10d;
        ushort compressor = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(10));
        ushort fan = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(12));
        if (temp is < -500 or > 500 || suction > 3 || discharge > 5 || current > 150 || compressor > 1 || fan > 1)
            throw new IOException("采集值超出演示点表量程，检查寄存器/缩放");
        return new(DateTimeOffset.UtcNow, Name, "Good", temp / 10d, suction, discharge, current,
            compressor == 1, fan == 1, watch.Elapsed.TotalMilliseconds, "");
    }
    public void Dispose() { }
}

public sealed class LoopbackPlc : IDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();
    private readonly Func<DemoScenario> scenario;
    private readonly byte unitId;
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly bool fragment;
    private readonly Task task;
    private int disposed;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public LoopbackPlc(int port, byte unitId, Func<DemoScenario> scenario, bool fragment = false)
    {
        this.scenario = scenario;
        this.unitId = unitId;
        this.fragment = fragment;
        listener = new(IPAddress.Loopback, port);
        listener.Start();
        // Keep the simulator accept loop off the WinForms synchronization context.
        // Dispose waits for cancellation; a captured UI context would deadlock here.
        task = Task.Run(ServeAsync);
    }
    private async Task ServeAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(2000);
                using var stream = client.GetStream();
                var request = new byte[12];
                await stream.ReadExactlyAsync(request, timeout.Token);
                if (scenario() == DemoScenario.LinkLost) continue;
                var valid = request[2] == 0 && request[3] == 0 && request[4] == 0 && request[5] == 6 &&
                    request[6] == unitId && request[7] == 3 && request[8] == 0 && request[9] == 0 && request[10] == 0 && request[11] == 6;
                var response = new byte[valid ? 21 : 9];
                request.AsSpan(0, 2).CopyTo(response);
                response[5] = valid ? (byte)15 : (byte)3;
                response[6] = request[6];
                response[7] = valid ? (byte)3 : (byte)0x83;
                response[8] = valid ? (byte)12 : (byte)2;
                if (valid)
                {
                    var s = SimulationSource.Create(watch.Elapsed.TotalSeconds, scenario(), "LOOPBACK");
                    var values = new ushort[] { unchecked((ushort)(short)Math.Round(s.Temperature!.Value * 10)),
                        (ushort)Math.Round(s.SuctionPressure!.Value * 1000), (ushort)Math.Round(s.DischargePressure!.Value * 1000),
                        (ushort)Math.Round(s.Current!.Value * 10), 1, 1 };
                    for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(9 + i * 2), values[i]);
                }
                if (fragment)
                {
                    foreach (var b in response) { await stream.WriteAsync(new[] { b }, timeout.Token); await Task.Delay(2, timeout.Token); }
                }
                else await stream.WriteAsync(response, timeout.Token);
            }
            catch (OperationCanceledException) { if (stop.IsCancellationRequested) break; }
            catch (IOException) { }
            catch (SocketException) { if (stop.IsCancellationRequested) break; }
            catch (ObjectDisposedException) { break; }
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        listener.Stop();
        task.GetAwaiter().GetResult();
        stop.Dispose();
    }
}

public sealed record AcquisitionUpdate(Sample Sample, long Total, long Good, long Bad, int ActiveAlarms, string StorageError);

public sealed class AcquisitionService(Repository repository)
{
    private CancellationTokenSource? stop;
    private Task? task;
    public event Action<AcquisitionUpdate>? Updated;
    public bool Running => task is { IsCompleted: false };
    public void Start(Settings settings, Func<DemoScenario> scenario)
    {
        if (Running) return;
        settings.Validate();
        stop = new();
        var token = stop.Token;
        task = Task.Run(async () =>
        {
            using IDataSource source = settings.Mode == AcquisitionMode.Simulation ? new SimulationSource(scenario) : new ModbusSource(settings);
            AlarmEngine engine;
            try { engine = new AlarmEngine(repository.ActiveCodes()); }
            catch (Exception ex)
            {
                Updated?.Invoke(new(Sample.Failure(source.Name, "读取数据库失败", 0), 0, 0, 0, 0, ex.Message));
                return;
            }
            long total = 0, good = 0, bad = 0;
            while (!token.IsCancellationRequested)
            {
                var clock = Stopwatch.StartNew();
                Sample s;
                try { s = await source.ReadAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
                { s = Sample.Failure(source.Name, ex is OperationCanceledException ? "采集超时（1500 ms）" : ex.Message, clock.Elapsed.TotalMilliseconds); }
                string storageError = "";
                int active = 0;
                try
                {
                    // Only publish successful storage after the transaction has committed.
                    var changes = engine.Evaluate(s, settings);
                    repository.Store(s, changes);
                    active = repository.ActiveCount();
                }
                catch (Exception ex)
                {
                    storageError = ex.Message;
                    try { engine = new AlarmEngine(repository.ActiveCodes()); }
                    catch { engine = new AlarmEngine(); }
                }
                total++;
                if (s.Quality == "Good") good++; else bad++;
                Updated?.Invoke(new(s, total, good, bad, active, storageError));
                try { await Task.Delay(Math.Max(1, settings.IntervalMs - (int)clock.ElapsedMilliseconds), token); }
                catch (OperationCanceledException) { break; }
                if (storageError.Length > 0) break;
            }
        }, token);
    }
    public async Task StopAsync()
    {
        if (stop == null) return;
        stop.Cancel();
        if (task != null) { try { await task; } catch (OperationCanceledException) { } }
        stop.Dispose(); stop = null; task = null;
    }
}
