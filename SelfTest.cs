using Microsoft.Data.Sqlite;

namespace RefrigerationHmi;

public static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RefrigerationHmi-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var lines = new List<string>();
        try
        {
            var settings = new Settings();
            var repository = new Repository(Path.Combine(directory, "test.db"));
            var engine = new AlarmEngine();
            var normal = SimulationSource.Create(0, DemoScenario.Normal, "TEST · SIM");
            repository.Store(normal, engine.Evaluate(normal, settings));
            Check(repository.Count() == 1 && repository.ActiveCount() == 0, "正常采集入库");
            var high = normal with { Time = normal.Time.AddSeconds(1), DischargePressure = 2.1 };
            repository.Store(high, engine.Evaluate(high, settings));
            repository.Store(high with { Time = high.Time.AddSeconds(1) }, engine.Evaluate(high, settings));
            Check(repository.Alarms().Count == 1 && repository.ActiveCount() == 1, "连续超限报警去重");
            var failure = Sample.Failure("TEST", "断线,带\"引号\"", 12);
            repository.Store(failure, engine.Evaluate(failure, settings));
            Check(repository.ActiveCount() == 2 && repository.Query(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, "Bad").Single().Temperature == null,
                "通信故障保存空值并保持过程报警");
            var alarm = repository.Alarms().Single(x => x.Code == "PRESS");
            repository.Acknowledge(alarm.Id);
            Check(repository.ActiveCount() == 2 && repository.Alarms().Single(x => x.Code == "PRESS").Acknowledged != null, "报警确认不解除故障");
            var hysteresis = high with { DischargePressure = 1.78 };
            var changes = engine.Evaluate(hysteresis, settings);
            Check(!changes.Any(x => x.Code == "PRESS" && !x.Active), "压力恢复回差");
            repository.Store(hysteresis, changes);
            repository.Store(normal, engine.Evaluate(normal, settings));
            Check(repository.ActiveCount() == 0 && repository.Alarms().All(x => x.Cleared != null), "有效采集恢复事件");
            repository.Store(high, engine.Evaluate(high, settings));
            Check(repository.Alarms().Count(x => x.Code == "PRESS") == 2, "恢复后再次触发");
            var restartedEngine = new AlarmEngine(repository.ActiveCodes());
            Check(restartedEngine.Evaluate(high, settings).Count == 0, "重启载入活动报警状态");
            var reopened = new Repository(repository.Path);
            Check(reopened.Count() == repository.Count() && reopened.ActiveCount() == 1, "数据库重新打开后持久化");
            var countBefore = repository.Count();
            try { repository.Store(normal, [new("PRESS", "duplicate", "故障", true)]); } catch (SqliteException) { }
            Check(repository.Count() == countBefore, "报警事务失败时采集记录回滚");
            var csvPath = Path.Combine(directory, "export.csv");
            int exported = repository.ExportCsv(csvPath, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, "All");
            Check(exported == repository.Count() && File.ReadAllText(csvPath).Contains("断线,带\"\"引号\"\""), "全量 CSV 与转义");
            var bulk = new Repository(Path.Combine(directory, "bulk.db"));
            using (var db = new SqliteConnection($"Data Source={bulk.Path}"))
            {
                db.Open(); using var command = db.CreateCommand();
                command.CommandText = """
                    WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<5101)
                    INSERT INTO samples(timestamp_utc,source,quality,temperature_c,suction_mpa,discharge_mpa,current_a,compressor,fan,latency_ms,error)
                    SELECT $time,'TEST BULK','Good',-18,0.28,1.32,23.6,1,1,1,'' FROM n;
                    """;
                command.Parameters.AddWithValue("$time", Repository.Iso(normal.Time)); command.ExecuteNonQuery();
            }
            Check(bulk.Query(DateTimeOffset.MinValue, DateTimeOffset.MaxValue).Count == 5000 &&
                bulk.ExportCsv(Path.Combine(directory, "bulk.csv"), DateTimeOffset.MinValue, DateTimeOffset.MaxValue, "All") == 5101,
                "历史显示限额与 CSV 全量分页导出");
            using (var plc = new LoopbackPlc(0, 1, () => DemoScenario.Normal, fragment: true))
            using (var source = new ModbusSource(settings with { Port = plc.Port }))
            {
                var tcp = await source.ReadAsync(CancellationToken.None);
                Check(tcp.Quality == "Good" && tcp.Temperature < -17 && tcp.SuctionPressure > .2 && tcp.Compressor == true,
                    "本机 Modbus TCP 分片报文与负温度解码");
                using var badUnit = new ModbusSource(settings with { Port = plc.Port, UnitId = 2 });
                bool rejected = false;
                try { await badUnit.ReadAsync(CancellationToken.None); } catch (IOException) { rejected = true; }
                Check(rejected, "Modbus 异常响应拒绝");
            }
            using (var plc = new LoopbackPlc(0, 1, () => DemoScenario.LinkLost))
            using (var source = new ModbusSource(settings with { Port = plc.Port }))
            {
                bool rejected = false;
                try { await source.ReadAsync(CancellationToken.None); } catch (IOException) { rejected = true; }
                Check(rejected, "Modbus 断线不生成有效样本");
            }
            var serviceRepo = new Repository(Path.Combine(directory, "service.db"));
            var service = new AcquisitionService(serviceRepo);
            var received = new TaskCompletionSource<AcquisitionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.Updated += update => { if (update.Total >= 3) received.TrySetResult(update); };
            service.Start(settings with { IntervalMs = 250 }, () => DemoScenario.Normal);
            var update = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.StopAsync(); var atStop = serviceRepo.Count(); await Task.Delay(350);
            Check(update.Total >= 3 && update.Good == update.Total && atStop >= 3 && serviceRepo.Count() == atStop && !service.Running,
                "采集循环落库与停止取消");
            var failedService = new AcquisitionService(serviceRepo);
            var failed = new TaskCompletionSource<AcquisitionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
            failedService.Updated += u => failed.TrySetResult(u);
            failedService.Start(settings, () => DemoScenario.LinkLost);
            var bad = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5)); await failedService.StopAsync();
            Check(bad.Bad == 1 && bad.Sample.Quality == "Bad" && bad.ActiveAlarms == 1, "采集异常进入质量和报警链路");
            var damagedRepo = new Repository(Path.Combine(directory, "damaged.db"));
            using (var db = new SqliteConnection($"Data Source={damagedRepo.Path}"))
            {
                db.Open(); using var command = db.CreateCommand(); command.CommandText = "DROP TABLE alarms;"; command.ExecuteNonQuery();
            }
            var storageService = new AcquisitionService(damagedRepo);
            var storageFailure = new TaskCompletionSource<AcquisitionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
            storageService.Updated += u => storageFailure.TrySetResult(u);
            storageService.Start(settings, () => DemoScenario.Normal);
            var storage = await storageFailure.Task.WaitAsync(TimeSpan.FromSeconds(5)); await storageService.StopAsync();
            Check(storage.StorageError.Length > 0 && damagedRepo.Count() == 0 && !storageService.Running, "数据库故障明确上报并停止采集");
            await CheckSimulatorUiThreadAsync();
            Check(true, "WinForms UI 线程启动、读取与停止 TCP 模拟器不死锁");
            lines.Add($"PASS {lines.Count} checks. Test data: {directory}");
            WriteReport(lines);
            return 0;
        }
        catch (Exception ex) { lines.Add("FAIL " + ex); WriteReport(lines); return 1; }
        void Check(bool condition, string name)
        { if (!condition) throw new Exception(name); lines.Add("PASS " + name); }
    }
    private static void WriteReport(List<string> lines)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "self-test-results.txt");
        File.WriteAllLines(file, lines, System.Text.Encoding.UTF8);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
    }
    private static async Task CheckSimulatorUiThreadAsync()
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new Form { ShowInTaskbar = false, Opacity = 0, Size = new(200, 100) };
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
                            throw new Exception("测试必须使用 WinForms 同步上下文");
                        using var plc = new LoopbackPlc(0, 1, () => DemoScenario.Normal);
                        using var source = new ModbusSource(new Settings { Port = plc.Port });
                        var sample = await source.ReadAsync(CancellationToken.None);
                        if (sample.Quality != "Good") throw new Exception("UI 线程 TCP 样本无效");
                        plc.Dispose();
                        completed.TrySetResult(true);
                    }
                    catch (Exception ex) { completed.TrySetException(ex); }
                    finally { form.BeginInvoke(form.Close); }
                };
                Application.Run(form);
            }
            catch (Exception ex) { completed.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(6));
    }
}
