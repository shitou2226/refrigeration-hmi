using System.Diagnostics;
using System.Globalization;

namespace RefrigerationHmi;

public sealed class MainForm : Form
{
    private Settings settings;
    private readonly Repository repository;
    private readonly AcquisitionService acquisition;
    private LoopbackPlc? plc;
    private int demoScenario;
    private bool busy, closing;
    private AcquisitionUpdate? latest;
    private readonly Label clockLabel = Label("", 9);
    private readonly Label sourceLabel = Label("● 模拟采集", 10, Theme.Green);
    private readonly Label pipelineLabel = Label("等待采集…", 9);
    private readonly Label footer = Label("", 9);
    private readonly Label alarmSummary = Label("", 10);
    private readonly Label historySummary = Label("", 9);
    private readonly Button startButton = Button("暂停采集", primary: true);
    private readonly Button simulatorButton = Button("启动 TCP 模拟器");
    private readonly ComboBox modeCombo = Combo(["模拟数据采集", "本机 Modbus TCP"], 170);
    private readonly ComboBox scenarioCombo = Combo(["正常运行", "排气高压故障", "通信中断故障"], 148);
    private readonly Panel content = new() { Dock = DockStyle.Fill };
    private readonly MetricCard temperature = new("冷库温度", "TT-01", "°C");
    private readonly MetricCard suction = new("吸气压力", "PT-01", "MPa");
    private readonly MetricCard discharge = new("排气压力", "PT-02", "MPa");
    private readonly MetricCard current = new("压缩机电流", "AI-04", "A");
    private readonly MetricCard stored = new("数据库记录", "SQLite", "条");
    private readonly TrendControl trend = new();
    private readonly PlantControl plant = new();
    private readonly DataGridView liveGrid = Grid();
    private readonly DataGridView historyGrid = Grid();
    private readonly DataGridView alarmGrid = Grid();
    private readonly DateTimePicker fromDate = DatePicker(DateTime.Now.Date);
    private readonly DateTimePicker toDate = DatePicker(DateTime.Now.Date.AddDays(1).AddTicks(-1));
    private readonly ComboBox qualityCombo = Combo(["全部质量", "Good", "Bad"], 100);
    private readonly CheckBox activeOnly = new() { Text = "仅看未恢复", AutoSize = true, Margin = new(16, 12, 10, 0) };
    private readonly NumericUpDown port = Number(1024, 65535, 15020);
    private readonly NumericUpDown unit = Number(1, 247, 1);
    private readonly NumericUpDown interval = Number(250, 10000, 1000);
    private readonly NumericUpDown tempLimit = Number(-30, 10, -15, 1);
    private readonly NumericUpDown pressureLimit = Number(.5m, 3, 1.8m, 2);
    private readonly Dictionary<string, Panel> pages = [];
    private readonly Dictionary<string, Button> navigation = [];
    private string selectedPage = "监控总览";
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };

    public MainForm()
    {
        settings = LocalFiles.LoadSettings();
        repository = new(LocalFiles.Database);
        acquisition = new(repository);
        Text = "制冷数据采集工作站 | C# · SQLite";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Font = Theme.Font(); BackColor = Theme.Background;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new(1420, 870);
        MinimumSize = new(1160, 770);
        var screen = Screen.PrimaryScreen!.WorkingArea;
        if (Height > screen.Height || Width > screen.Width) WindowState = FormWindowState.Maximized;

        var root = Table(2, 1); root.ColumnStyles.Add(new(SizeType.Absolute, 172)); root.ColumnStyles.Add(new(SizeType.Percent, 100));
        Controls.Add(root);
        root.Controls.Add(BuildSidebar(), 0, 0);
        var main = Table(1, 4); main.Padding = new(22, 14, 22, 10);
        main.RowStyles.Add(new(SizeType.Absolute, 74)); main.RowStyles.Add(new(SizeType.Absolute, 52));
        main.RowStyles.Add(new(SizeType.Percent, 100)); main.RowStyles.Add(new(SizeType.Absolute, 34));
        root.Controls.Add(main, 1, 0);
        main.Controls.Add(BuildHeader(), 0, 0); main.Controls.Add(BuildToolbar(), 0, 1);
        main.Controls.Add(content, 0, 2); main.Controls.Add(footer, 0, 3);
        pages["监控总览"] = BuildOverview(); pages["历史数据"] = BuildHistory();
        pages["报警事件"] = BuildAlarms(); pages["采集配置"] = BuildConfiguration();
        foreach (var page in pages.Values) { page.Dock = DockStyle.Fill; content.Controls.Add(page); }
        ApplyConfigFields();
        modeCombo.SelectedIndex = (int)settings.Mode;
        modeCombo.SelectedIndexChanged += async (_, _) => await ChangeModeAsync();
        scenarioCombo.SelectedIndexChanged += (_, _) => Volatile.Write(ref demoScenario, scenarioCombo.SelectedIndex);
        startButton.Click += async (_, _) => await ToggleAcquisitionAsync();
        simulatorButton.Click += async (_, _) => await ToggleSimulatorAsync();
        acquisition.Updated += update =>
        {
            if (IsDisposed || closing || !IsHandleCreated) return;
            try { BeginInvoke(() => DisplayUpdate(update)); } catch (InvalidOperationException) { }
        };
        timer.Tick += (_, _) => RefreshStatus(); timer.Start();
        Shown += (_, _) =>
        {
            foreach (var grid in new[] { liveGrid, historyGrid, alarmGrid })
            {
                float factor = DeviceDpi / 96f;
                foreach (DataGridViewColumn column in grid.Columns) column.Width = (int)Math.Round(column.Width * factor);
                grid.ColumnHeadersHeight = (int)Math.Round(34 * factor);
                grid.RowTemplate.Height = (int)Math.Round(32 * factor);
            }
            liveGrid.Columns[^1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            SelectPage("监控总览"); StartAcquisition();
        };
        FormClosing += async (_, e) =>
        {
            if (closing) return;
            e.Cancel = true; closing = true; Enabled = false; timer.Stop();
            await acquisition.StopAsync(); plc?.Dispose(); timer.Dispose();
            // StopAsync may complete synchronously when already paused. Defer Close
            // until the first canceled FormClosing event has returned.
            if (!IsDisposed && IsHandleCreated) BeginInvoke(Close);
        };
        // Programmatic controls are authored at 96 DPI; scale only after the tree exists.
        AutoScaleDimensions = new SizeF(96, 96);
        PerformAutoScale();
        if (Height > screen.Height || Width > screen.Width) WindowState = FormWindowState.Maximized;
    }
    private Panel BuildSidebar()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(25, 43, 65) };
        var brand = Label("❄  COLDLINK", 12, Color.White, true); brand.Dock = DockStyle.None; brand.SetBounds(19, 28, 146, 34); panel.Controls.Add(brand);
        var sub = Label("制冷机组监控", 9, Color.FromArgb(150, 171, 193)); sub.Dock = DockStyle.None; sub.SetBounds(23, 63, 140, 26); panel.Controls.Add(sub);
        var section = Label("工作空间", 8, Color.FromArgb(135, 155, 180)); section.Dock = DockStyle.None; section.SetBounds(23, 127, 130, 25); panel.Controls.Add(section);
        var names = new[] { "监控总览", "历史数据", "报警事件", "采集配置" };
        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i]; var button = Button($"{i + 1:00}   {name}");
            button.SetBounds(12, 165 + i * 55, 148, 43); button.TextAlign = ContentAlignment.MiddleLeft;
            button.Padding = new(9, 0, 0, 0); button.FlatAppearance.BorderSize = 0;
            button.Click += (_, _) => SelectPage(name); navigation[name] = button; panel.Controls.Add(button);
        }
        var foot = Label("LOCAL WORKSTATION\n\nC# WinForms + SQLite\n学习 / 面试演示项目", 8.5f, Color.FromArgb(150, 171, 193));
        foot.Dock = DockStyle.Bottom; foot.Height = 120; foot.Padding = new(22, 0, 0, 20); panel.Controls.Add(foot);
        return panel;
    }
    private Panel BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        var title = Label("制冷数据采集工作站", 21, Theme.Ink, true); title.Dock = DockStyle.None; title.SetBounds(0, 0, 570, 39); panel.Controls.Add(title);
        var subtitle = Label("01 号冷库机组  /  温度、压力与电流连续采集", 9, Theme.Muted); subtitle.Dock = DockStyle.None; subtitle.SetBounds(2, 42, 580, 26); panel.Controls.Add(subtitle);
        var right = Table(1, 2); right.Dock = DockStyle.Right; right.Width = 240;
        sourceLabel.TextAlign = ContentAlignment.MiddleRight; clockLabel.TextAlign = ContentAlignment.MiddleRight;
        right.Controls.Add(sourceLabel, 0, 0); right.Controls.Add(clockLabel, 0, 1); panel.Controls.Add(right);
        return panel;
    }
    private Panel BuildToolbar()
    {
        var flow = Flow(); flow.Padding = new(0, 4, 0, 0);
        flow.Controls.Add(modeCombo); flow.Controls.Add(startButton);
        flow.Controls.Add(LabelInline("故障演示", 68)); flow.Controls.Add(scenarioCombo); flow.Controls.Add(simulatorButton);
        return flow;
    }
    private Panel BuildOverview()
    {
        var panel = new Panel(); var layout = Table(1, 4); panel.Controls.Add(layout);
        layout.RowStyles.Add(new(SizeType.Absolute, 116)); layout.RowStyles.Add(new(SizeType.Percent, 57));
        layout.RowStyles.Add(new(SizeType.Absolute, 60)); layout.RowStyles.Add(new(SizeType.Percent, 43));
        var metrics = Table(5, 1); metrics.Margin = new(0, 0, 0, 12);
        foreach (var card in new[] { temperature, suction, discharge, current, stored })
        {
            metrics.ColumnStyles.Add(new(SizeType.Percent, 20)); card.Dock = DockStyle.Fill; card.Margin = new(0, 0, 10, 0); metrics.Controls.Add(card);
        }
        stored.Margin = Padding.Empty; layout.Controls.Add(metrics, 0, 0);
        var middle = Table(2, 1); middle.Margin = Padding.Empty;
        middle.ColumnStyles.Add(new(SizeType.Percent, 47)); middle.ColumnStyles.Add(new(SizeType.Percent, 53));
        plant.Dock = DockStyle.Fill; plant.Margin = new(0, 0, 12, 0); trend.Dock = DockStyle.Fill; trend.Margin = Padding.Empty;
        middle.Controls.Add(plant, 0, 0); middle.Controls.Add(trend, 1, 0); layout.Controls.Add(middle, 0, 1);
        var pipeline = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(231, 238, 247), Margin = new(0, 12, 0, 8), Padding = new(14, 0, 14, 0) };
        pipelineLabel.Dock = DockStyle.Fill; pipeline.Controls.Add(pipelineLabel); layout.Controls.Add(pipeline, 0, 2);
        var table = Table(1, 2); table.BackColor = Color.White; table.RowStyles.Add(new(SizeType.Absolute, 38)); table.RowStyles.Add(new(SizeType.Percent, 100));
        var heading = Label("  最新采集记录  /  实际轮询生成，自动写入数据库", 10, Theme.Ink, true);
        table.Controls.Add(heading, 0, 0); AddSampleColumns(liveGrid, false); table.Controls.Add(liveGrid, 0, 1); layout.Controls.Add(table, 0, 3);
        return panel;
    }
    private Panel BuildHistory()
    {
        var panel = new Panel(); var layout = Table(1, 4); panel.Controls.Add(layout);
        layout.RowStyles.Add(new(SizeType.Absolute, 58)); layout.RowStyles.Add(new(SizeType.Absolute, 56));
        layout.RowStyles.Add(new(SizeType.Absolute, 33)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(Label("历史采集数据", 17, Theme.Ink, true), 0, 0);
        var filters = Flow(); filters.Controls.Add(fromDate); filters.Controls.Add(LabelInline("至", 25)); filters.Controls.Add(toDate);
        filters.Controls.Add(qualityCombo);
        var query = Button("查询记录", true); query.Click += async (_, _) => await QueryHistoryAsync(); filters.Controls.Add(query);
        var export = Button("导出 CSV"); export.Click += async (_, _) => await ExportAsync(); filters.Controls.Add(export);
        layout.Controls.Add(filters, 0, 1); layout.Controls.Add(historySummary, 0, 2);
        AddSampleColumns(historyGrid, true); layout.Controls.Add(historyGrid, 0, 3);
        return panel;
    }
    private Panel BuildAlarms()
    {
        var panel = new Panel(); var layout = Table(1, 4); panel.Controls.Add(layout);
        layout.RowStyles.Add(new(SizeType.Absolute, 58)); layout.RowStyles.Add(new(SizeType.Absolute, 56));
        layout.RowStyles.Add(new(SizeType.Absolute, 38)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(Label("报警与事件记录", 17, Theme.Ink, true), 0, 0);
        var actions = Flow(); var refresh = Button("刷新事件"); refresh.Click += (_, _) => LoadAlarms(); actions.Controls.Add(refresh);
        var ack = Button("确认选中报警", true);
        ack.Click += (_, _) =>
        {
            if (alarmGrid.CurrentRow?.Tag is not AlarmEvent alarm) return;
            try { repository.Acknowledge(alarm.Id); LoadAlarms(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "确认失败"); }
        };
        actions.Controls.Add(ack); actions.Controls.Add(activeOnly); activeOnly.CheckedChanged += (_, _) => LoadAlarms();
        layout.Controls.Add(actions, 0, 1); layout.Controls.Add(alarmSummary, 0, 2);
        foreach (var (name, width) in new[] { ("编号", 55), ("报警名称", 160), ("等级", 60), ("状态", 75), ("发生时间", 156), ("恢复时间", 156), ("确认时间", 156), ("数据来源", 160) })
            alarmGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, Width = width });
        layout.Controls.Add(alarmGrid, 0, 3); return panel;
    }
    private Panel BuildConfiguration()
    {
        var panel = new Panel(); var layout = Table(1, 3); panel.Controls.Add(layout);
        layout.RowStyles.Add(new(SizeType.Absolute, 58)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 68));
        layout.Controls.Add(Label("采集参数与点表", 17, Theme.Ink, true), 0, 0);
        var split = Table(2, 1); split.ColumnStyles.Add(new(SizeType.Absolute, 395)); split.ColumnStyles.Add(new(SizeType.Percent, 100));
        var fields = Table(2, 8); fields.BackColor = Color.White; fields.Padding = new(20);
        fields.ColumnStyles.Add(new(SizeType.Absolute, 178)); fields.ColumnStyles.Add(new(SizeType.Percent, 100));
        int row = 0;
        Add("Modbus 地址", Label("127.0.0.1（本机）", 10)); Add("TCP 端口", port); Add("站号 Unit ID", unit);
        Add("采集周期 / ms", interval); Add("温度预警 / °C", tempLimit); Add("排气高压 / MPa", pressureLimit);
        var save = Button("保存参数并重启采集", true); save.Width = 175;
        save.Click += async (_, _) => await SaveConfigurationAsync(); fields.Controls.Add(save, 0, row); fields.SetColumnSpan(save, 2);
        split.Controls.Add(fields, 0, 0);
        var mapping = Label("演示 Modbus 点表\n\n功能码 03 · 起始地址 0 · 连续读取 6 个寄存器\n\n40001 / 地址 0   冷库温度   INT16 × 0.1 °C\n40002 / 地址 1   吸气压力   UINT16 × 0.001 MPa\n40003 / 地址 2   排气压力   UINT16 × 0.001 MPa\n40004 / 地址 3   压缩机电流 UINT16 × 0.1 A\n40005 / 地址 4   压缩机状态 0 停止 / 1 运行\n40006 / 地址 5   风机状态   0 停止 / 1 运行\n\n双字节大端；温度使用有符号数。\n本机 TCP 模拟器和本点表配套，可演示真实 TCP 报文采集。\n\n这些地址、量程与阈值仅用于本项目演示。\n接入真实 PLC 前需按设备手册核对点表。\n设备联锁、急停及启停控制由 PLC 完成。", 10);
        mapping.BackColor = Color.White; mapping.TextAlign = ContentAlignment.TopLeft; mapping.Padding = new(22); mapping.Margin = new(12, 0, 0, 0);
        split.Controls.Add(mapping, 1, 0); layout.Controls.Add(split, 0, 1);
        var db = Label($"本地数据库：{repository.Path}\n表：samples（采集记录） / alarms（报警发生、恢复、确认） · UTC 存储 / 本地时间显示", 8.5f);
        layout.Controls.Add(db, 0, 2); return panel;
        void Add(string title, Control control)
        {
            fields.RowStyles.Add(new(SizeType.Absolute, 52)); fields.Controls.Add(Label(title, 10), 0, row);
            control.Margin = new(0, 9, 5, 0); fields.Controls.Add(control, 1, row++);
        }
    }
    private void SelectPage(string page)
    {
        selectedPage = page;
        foreach (var (name, panel) in pages) panel.Visible = name == page;
        pages[page].BringToFront();
        foreach (var (name, button) in navigation)
        {
            button.BackColor = name == page ? Color.FromArgb(47, 71, 101) : Color.FromArgb(25, 43, 65);
            button.ForeColor = name == page ? Color.White : Color.FromArgb(170, 188, 208);
        }
        if (page == "历史数据") _ = QueryHistoryAsync();
        if (page == "报警事件") LoadAlarms();
    }
    private void StartAcquisition()
    {
        if (closing) return;
        acquisition.Start(settings, () => (DemoScenario)Volatile.Read(ref demoScenario));
        startButton.Text = "暂停采集"; plant.Paused = false;
        sourceLabel.Text = settings.Mode == AcquisitionMode.Simulation ? "● 模拟采集 · SIM" : $"● 本机 TCP : {settings.Port}";
        trend.TemperatureLimit = settings.HighTemperature; plant.HighPressure = settings.HighPressure;
    }
    private async Task ToggleAcquisitionAsync()
    {
        if (busy) return; busy = true;
        try
        {
            if (acquisition.Running) { await acquisition.StopAsync(); MarkPaused(); }
            else StartAcquisition();
        }
        finally { busy = false; }
    }
    private void MarkPaused()
    {
        startButton.Text = "开始采集"; sourceLabel.Text = "● 采集已暂停"; sourceLabel.ForeColor = Theme.Muted;
        plant.Paused = true; plant.Invalidate(); pipelineLabel.Text = "采集已暂停  ·  屏幕数值为最后一条记录，可在历史数据中查询。";
    }
    private async Task ChangeModeAsync()
    {
        if (busy) { modeCombo.SelectedIndex = (int)settings.Mode; return; }
        busy = true;
        try
        {
            await acquisition.StopAsync();
            var candidate = settings with { Mode = (AcquisitionMode)modeCombo.SelectedIndex };
            LocalFiles.SaveSettings(candidate); settings = candidate;
            latest = null; trend.Samples.Clear(); liveGrid.Rows.Clear(); plant.Sample = null;
            foreach (var card in new[] { temperature, suction, discharge, current }) { card.Value = "—"; card.Detail = "等待当前数据源采集"; card.Alarm = false; card.Invalidate(); }
            StartAcquisition();
        }
        catch (Exception ex) { MarkPaused(); MessageBox.Show(this, ex.Message, "切换数据源失败"); }
        finally { busy = false; }
    }
    private async Task ToggleSimulatorAsync()
    {
        if (busy) return; busy = true;
        try
        {
            if (plc != null) { plc.Dispose(); plc = null; simulatorButton.Text = "启动 TCP 模拟器"; }
            else { plc = new(settings.Port, settings.UnitId, () => (DemoScenario)Volatile.Read(ref demoScenario)); simulatorButton.Text = "停止 TCP 模拟器"; }
            await Task.CompletedTask;
        }
        catch (Exception ex) { MessageBox.Show(this, $"本机模拟器：{ex.Message}\n端口可能已被占用，请在采集配置中更换。", "启动失败"); }
        finally { busy = false; }
    }
    private void ApplyConfigFields()
    {
        port.Value = settings.Port; unit.Value = settings.UnitId; interval.Value = settings.IntervalMs;
        tempLimit.Value = (decimal)settings.HighTemperature; pressureLimit.Value = (decimal)settings.HighPressure;
    }
    private async Task SaveConfigurationAsync()
    {
        if (busy) return; busy = true;
        try
        {
            var candidate = settings with { Port = (int)port.Value, UnitId = (byte)unit.Value, IntervalMs = (int)interval.Value,
                HighTemperature = (double)tempLimit.Value, HighPressure = (double)pressureLimit.Value };
            candidate.Validate(); await acquisition.StopAsync(); LocalFiles.SaveSettings(candidate);
            bool restartPlc = plc != null; plc?.Dispose(); plc = null; settings = candidate;
            simulatorButton.Text = "启动 TCP 模拟器";
            if (restartPlc) { plc = new(settings.Port, settings.UnitId, () => (DemoScenario)Volatile.Read(ref demoScenario)); simulatorButton.Text = "停止 TCP 模拟器"; }
            StartAcquisition(); footer.Text = "参数已保存；当前采集已按新参数重新启动。";
        }
        catch (Exception ex) { MarkPaused(); MessageBox.Show(this, ex.Message, "参数保存失败"); }
        finally { busy = false; }
    }
    private void DisplayUpdate(AcquisitionUpdate update)
    {
        if (closing) return;
        latest = update; var s = update.Sample;
        bool good = s.Quality == "Good";
        void Metric(MetricCard card, double? value, string format, string detail, bool alarm = false)
        { card.Value = value?.ToString(format) ?? "—"; card.Detail = good ? detail : "无效数据 · 不沿用旧值"; card.Alarm = alarm; card.Invalidate(); }
        Metric(temperature, s.Temperature, "0.0", $"预警上限 {settings.HighTemperature:0.#} °C", good && s.Temperature > settings.HighTemperature);
        Metric(suction, s.SuctionPressure, "0.000", "工程量转换 × 0.001");
        Metric(discharge, s.DischargePressure, "0.000", $"预警上限 {settings.HighPressure:0.##} MPa", good && s.DischargePressure > settings.HighPressure);
        Metric(current, s.Current, "0.0", "工程量转换 × 0.1");
        stored.Value = update.StorageError.Length == 0 ? repository.Count().ToString("N0") : "故障";
        stored.Detail = update.StorageError.Length == 0 ? "已提交 · 采集后自动入库" : "写入失败 · 采集已停止";
        stored.Alarm = update.StorageError.Length > 0; stored.Invalidate();
        trend.Samples.Add(s); trend.Samples.RemoveAll(x => x.Time < s.Time.AddSeconds(-125)); trend.Invalidate();
        plant.Sample = s; plant.Paused = false; plant.Invalidate();
        liveGrid.Rows.Insert(0, SampleValues(s, false));
        if (liveGrid.Rows.Count > 100) liveGrid.Rows.RemoveAt(liveGrid.Rows.Count - 1);
        if (!good) liveGrid.Rows[0].DefaultCellStyle.ForeColor = Theme.Red;
        sourceLabel.ForeColor = good ? Theme.Green : Theme.Red;
        pipelineLabel.ForeColor = update.StorageError.Length > 0 || !good ? Theme.Red : Theme.Ink;
        var rate = update.Total == 0 ? 0 : update.Good * 100d / update.Total;
        pipelineLabel.Text = update.StorageError.Length > 0 ? $"数据库写入失败：{update.StorageError}" :
            $"{(settings.Mode == AcquisitionMode.Simulation ? "SIM 演示源" : "Modbus TCP")}   →   C# 采集 / 工程量转换   →   SQLite 已存储    |    本次 {update.Total} 条   有效率 {rate:0.0}%   延迟 {s.LatencyMs:0.0} ms";
        if (update.StorageError.Length > 0) { startButton.Text = "开始采集"; sourceLabel.Text = "● 存储故障 · 已停止"; }
        if (selectedPage == "报警事件") LoadAlarms();
        RefreshStatus();
    }
    private void RefreshStatus()
    {
        clockLabel.Text = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");
        if (latest == null) { footer.Text = "准备采集  ·  数据来源与质量随每条记录保存"; return; }
        var s = latest.Sample;
        if (latest.StorageError.Length > 0) { footer.Text = "存储异常：" + latest.StorageError; footer.ForeColor = Theme.Red; return; }
        var stale = (DateTimeOffset.UtcNow - s.Time).TotalMilliseconds > Math.Max(3000, settings.IntervalMs * 2.5);
        footer.ForeColor = !acquisition.Running ? Theme.Muted : latest.ActiveAlarms > 0 || stale ? Theme.Red : Theme.Muted;
        footer.Text = $"{(acquisition.Running ? "采集中" : "已暂停")}   |   最新 {s.Time.ToLocalTime():HH:mm:ss}   质量 {s.Quality}" +
            $"   |   活动报警 {latest.ActiveAlarms}   |   本次失败 {latest.Bad} 条" + (stale && acquisition.Running ? "   |   数据已过期" : "") +
            (s.Error.Length > 0 ? "   |   " + s.Error : "   |   数据存储在本机");
    }
    private async Task QueryHistoryAsync()
    {
        if (closing) return;
        var (start, end, quality) = Filter();
        historySummary.Text = "查询中…";
        try
        {
            var samples = await Task.Run(() => repository.Query(start, end, quality));
            if (closing) return;
            historyGrid.Rows.Clear();
            foreach (var s in samples) historyGrid.Rows.Add(SampleValues(s, true));
            historySummary.Text = $"查询结果 {samples.Count:N0} 条 · 界面最多显示最新 5,000 条，CSV 导出所选范围全部记录 · 时间为本地时间";
        }
        catch (Exception ex) { historySummary.Text = "查询失败：" + ex.Message; }
    }
    private (DateTimeOffset Start, DateTimeOffset End, string Quality) Filter() =>
        (new(fromDate.Value), new(toDate.Value), qualityCombo.SelectedIndex == 0 ? "All" : qualityCombo.Text);
    private async Task ExportAsync()
    {
        var (start, end, quality) = Filter();
        if (start > end) { historySummary.Text = "开始时间不能晚于结束时间。"; return; }
        using var dialog = new SaveFileDialog { Filter = "CSV 文件|*.csv", FileName = $"制冷采集_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            InitialDirectory = LocalFiles.Root };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            int count = await Task.Run(() => repository.ExportCsv(dialog.FileName, start, end, quality));
            if (!closing) historySummary.Text = $"已导出 {count:N0} 条：{dialog.FileName}（UTF-8，UTC 时间）";
        }
        catch (Exception ex) { if (!closing) historySummary.Text = "导出失败：" + ex.Message; }
    }
    private void LoadAlarms()
    {
        try
        {
            long? selectedId = (alarmGrid.CurrentRow?.Tag as AlarmEvent)?.Id;
            var alarms = repository.Alarms(activeOnly.Checked); alarmGrid.Rows.Clear();
            foreach (var a in alarms)
            {
                int index = alarmGrid.Rows.Add(a.Id, a.Name, a.Severity, a.Cleared == null ? "活动" : "已恢复",
                    a.Raised.ToLocalTime().ToString("MM-dd HH:mm:ss"), a.Cleared?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "—",
                    a.Acknowledged?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "未确认", a.Source);
                alarmGrid.Rows[index].Tag = a;
                if (a.Cleared == null) alarmGrid.Rows[index].DefaultCellStyle.ForeColor = Theme.Red;
                if (a.Id == selectedId) alarmGrid.CurrentCell = alarmGrid.Rows[index].Cells[0];
            }
            alarmSummary.Text = $"活动报警 {repository.ActiveCount()} 条 · 确认表示已阅，不解除故障 · 连续超限只产生一条事件，恢复后可再次触发";
        }
        catch (Exception ex) { alarmSummary.Text = "事件读取失败：" + ex.Message; }
    }
    private static object[] SampleValues(Sample s, bool detailed)
    {
        object[] values = [s.Time.ToLocalTime().ToString(detailed ? "MM-dd HH:mm:ss.fff" : "HH:mm:ss.fff"),
            s.Quality, s.Temperature?.ToString("0.0") ?? "—", s.SuctionPressure?.ToString("0.000") ?? "—",
            s.DischargePressure?.ToString("0.000") ?? "—", s.Current?.ToString("0.0") ?? "—",
            s.Compressor == null ? "未知" : s.Compressor.Value ? "运行" : "停止",
            s.Fan == null ? "未知" : s.Fan.Value ? "运行" : "停止", s.LatencyMs.ToString("0.0"), s.Source];
        return detailed ? [.. values, s.Error] : values;
    }
    private static void AddSampleColumns(DataGridView grid, bool detailed)
    {
        var columns = new[] { ("采集时间", detailed ? 170 : 115), ("质量", 60), ("温度 °C", 85), ("吸气 MPa", 92),
            ("排气 MPa", 92), ("电流 A", 75), ("压缩机", 75), ("风机", 65), ("延迟 ms", 80), ("数据来源", 180) };
        foreach (var (name, width) in columns) grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, Width = width });
        if (detailed) grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "错误说明", Width = 260 });
    }
    private static TableLayoutPanel Table(int columns, int rows) => new()
    { ColumnCount = columns, RowCount = rows, Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty };
    private static Panel Flow() => new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = true };
    private static Label Label(string text, float size = 10, Color? color = null, bool bold = false) => new()
    { Text = text, Dock = DockStyle.Fill, Font = Theme.Font(size, bold), ForeColor = color ?? Theme.Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = Padding.Empty };
    private static Label LabelInline(string text, int width) => new()
    { Text = text, Width = width, Height = 34, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Muted, Margin = new(0, 0, 5, 0) };
    private static Button Button(string text, bool primary = false) => new()
    { Text = text, AutoSize = false, Width = text.Length > 8 ? 154 : 108, Height = 35, FlatStyle = FlatStyle.Flat,
        BackColor = primary ? Theme.Blue : Color.White, ForeColor = primary ? Color.White : Theme.Ink,
        Font = Theme.Font(9), Margin = new(0, 0, 10, 0), Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
    private static ComboBox Combo(string[] items, int width)
    {
        var control = new ComboBox { Width = width, DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Font(9), Margin = new(0, 2, 10, 0) };
        control.Items.AddRange(items); control.SelectedIndex = 0; return control;
    }
    private static NumericUpDown Number(decimal min, decimal max, decimal value, int decimals = 0) => new()
    { Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals, Increment = decimals == 0 ? 1 : decimals == 1 ? .1m : .01m,
        Width = 147, Font = Theme.Font(10) };
    private static DateTimePicker DatePicker(DateTime value) => new()
    { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss", Value = value, Width = 183, Font = Theme.Font(9), Margin = new(0, 2, 6, 0) };
    private static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
        BackgroundColor = Color.White, BorderStyle = BorderStyle.None, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
        GridColor = Theme.Line, EnableHeadersVisualStyles = false, ColumnHeadersHeight = 34,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing, RowTemplate = { Height = 32 },
        ColumnHeadersDefaultCellStyle = new() { BackColor = Color.FromArgb(237, 242, 248), ForeColor = Theme.Muted, Font = Theme.Font(9), Padding = new(5, 0, 0, 0) },
        DefaultCellStyle = new() { ForeColor = Theme.Ink, Font = Theme.Font(9), SelectionBackColor = Color.FromArgb(224, 235, 249), SelectionForeColor = Theme.Ink, Padding = new(5, 0, 0, 0) },
        AlternatingRowsDefaultCellStyle = new() { BackColor = Color.FromArgb(248, 250, 253) }
    };
}
