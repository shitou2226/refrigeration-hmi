using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace RefrigerationHmi;

public static class Theme
{
    public static readonly Color Ink = Color.FromArgb(32, 47, 66);
    public static readonly Color Muted = Color.FromArgb(112, 126, 144);
    public static readonly Color Blue = Color.FromArgb(47, 102, 174);
    public static readonly Color Green = Color.FromArgb(39, 128, 106);
    public static readonly Color Red = Color.FromArgb(196, 67, 67);
    public static readonly Color Background = Color.FromArgb(241, 244, 248);
    public static readonly Color Line = Color.FromArgb(225, 232, 240);
    public static Font Font(float size = 10, bool bold = false) => new("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
    public static void Text(Graphics g, string text, float size, Color color, RectangleF rect, bool bold = false,
        StringAlignment alignment = StringAlignment.Near)
    {
        using var font = new Font("Microsoft YaHei UI", size * 96 / 72, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Alignment = alignment, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        g.DrawString(text, font, brush, rect, format);
    }
}

public sealed class MetricCard(string title, string tag, string unit) : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Value { get; set; } = "—";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Detail { get; set; } = "等待第一条采集";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Alarm { get; set; }
    public MetricCard() : this("", "", "") { }
    protected override void OnCreateControl() { base.OnCreateControl(); DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Color.White);
        float scale = DeviceDpi / 96f; g.ScaleTransform(scale, scale);
        float width = Width / scale, height = Height / scale;
        using var line = new Pen(Theme.Line); g.DrawRectangle(line, 0, 0, width - 1, height - 1);
        using var accent = new SolidBrush(Alarm ? Theme.Red : Theme.Blue); g.FillRectangle(accent, 0, 0, 3, height);
        Theme.Text(g, title, 10, Theme.Muted, new(16, 8, width - 32, 23));
        Theme.Text(g, tag, 8, Theme.Muted, new(width - 80, 9, 66, 20), alignment: StringAlignment.Far);
        float size = width < 190 ? 23 : 27;
        Theme.Text(g, Value, size, Alarm ? Theme.Red : Theme.Ink, new(15, 31, width - 70, 46), true);
        Theme.Text(g, unit, 10, Theme.Muted, new(width - 52, 41, 45, 27));
        Theme.Text(g, Detail, 8.5f, Alarm ? Theme.Red : Theme.Muted, new(16, height - 27, width - 28, 22));
    }
}

public sealed class TrendControl : Control
{
    public List<Sample> Samples { get; } = [];
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double TemperatureLimit { get; set; } = -15;
    public TrendControl() { DoubleBuffered = true; BackColor = Color.White; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f; g.ScaleTransform(scale, scale);
        float width = Width / scale, height = Height / scale;
        Theme.Text(g, "实时趋势", 11, Theme.Ink, new(18, 10, 130, 24), true);
        Theme.Text(g, "● 温度 °C", 9, Theme.Blue, new(width - 235, 12, 108, 22));
        Theme.Text(g, "● 排气 MPa", 9, Theme.Green, new(width - 120, 12, 112, 22));
        var plot = new RectangleF(48, 58, width - 98, height - 112);
        if (plot.Width <= 0 || plot.Height <= 0) return;
        using var grid = new Pen(Theme.Line);
        for (int i = 0; i <= 5; i++)
        {
            var y = plot.Top + i * plot.Height / 5;
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            Theme.Text(g, (5 - i * 7).ToString(), 8, Theme.Muted, new(3, y - 9, 36, 18), alignment: StringAlignment.Far);
            Theme.Text(g, (2.5 - i * .5).ToString("0.0"), 8, Theme.Muted, new(plot.Right + 6, y - 9, 39, 18));
        }
        var end = Samples.Count > 0 ? Samples[^1].Time : DateTimeOffset.UtcNow;
        var start = end.AddSeconds(-120);
        for (int i = 0; i <= 4; i++)
        {
            var x = plot.Left + i * plot.Width / 4;
            g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
            Theme.Text(g, start.AddSeconds(i * 30).ToLocalTime().ToString("HH:mm:ss"), 8, Theme.Muted, new(x - 32, plot.Bottom + 5, 66, 22), alignment: StringAlignment.Center);
        }
        float Y(double value, double min, double max) => plot.Bottom - (float)((Math.Clamp(value, min, max) - min) / (max - min) * plot.Height);
        using var threshold = new Pen(Color.FromArgb(192, 139, 67)) { DashStyle = DashStyle.Dash };
        float thresholdY = Y(TemperatureLimit, -30, 5);
        g.DrawLine(threshold, plot.Left, thresholdY, plot.Right, thresholdY);
        Theme.Text(g, $"温度预警 {TemperatureLimit:0.#} °C", 8, Color.FromArgb(158, 110, 49), new(plot.Left + 6, thresholdY - 23, 150, 21));
        Line(s => s.Temperature, -30, 5, Theme.Blue);
        Line(s => s.DischargePressure, 0, 2.5, Theme.Green);
        Theme.Text(g, Samples.Count == 0 ? "等待采集数据，曲线随采集生成" : "最近 120 秒 · 固定量程 · 断线处留空", 8.5f, Theme.Muted, new(18, height - 27, width - 36, 22));
        void Line(Func<Sample, double?> value, double min, double max, Color color)
        {
            using var pen = new Pen(color, 2);
            PointF? previous = null;
            foreach (var sample in Samples.Where(s => s.Time >= start))
            {
                var v = value(sample);
                if (sample.Quality != "Good" || v == null) { previous = null; continue; }
                var point = new PointF(plot.Left + (float)((sample.Time - start).TotalSeconds / 120 * plot.Width), Y(v.Value, min, max));
                if (previous != null) g.DrawLine(pen, previous.Value, point);
                previous = point;
            }
            if (previous != null) { using var brush = new SolidBrush(color); g.FillEllipse(brush, previous.Value.X - 3, previous.Value.Y - 3, 6, 6); }
        }
    }
}

public sealed class PlantControl : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Sample? Sample { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Paused { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double HighPressure { get; set; } = 1.8;
    public PlantControl() { DoubleBuffered = true; BackColor = Color.White; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f; g.ScaleTransform(scale, scale);
        float width = Width / scale, height = Height / scale;
        Theme.Text(g, "制冷机组 / 01", 11, Theme.Ink, new(18, 10, 200, 25), true);
        Theme.Text(g, "单级循环示意", 8.5f, Theme.Muted, new(width - 150, 12, 130, 22), alignment: StringAlignment.Far);
        float sx = width / 520f, sy = (height - 48) / 280f;
        var state = g.Save(); g.TranslateTransform(0, 40); g.ScaleTransform(sx, sy);
        using var pipe = new Pen(Color.FromArgb(157, 173, 190), 4);
        g.DrawLines(pipe, new[] { new PointF(118, 65), new(402, 65), new(402, 215), new(118, 215), new(118, 65) });
        using var arrow = new Pen(Theme.Muted, 2) { EndCap = LineCap.ArrowAnchor };
        g.DrawLine(arrow, 250, 65, 280, 65); g.DrawLine(arrow, 280, 215, 250, 215);
        g.DrawLine(arrow, 402, 125, 402, 147); g.DrawLine(arrow, 118, 154, 118, 128);
        bool valid = Sample?.Quality == "Good" && !Paused;
        bool high = valid && Sample?.DischargePressure > HighPressure;
        Device(new(47, 28, 142, 73), "压缩机", valid ? (Sample!.Compressor == true ? "运行" : "停止") : "状态未知", valid && Sample!.Compressor == true, high);
        Device(new(324, 28, 154, 73), "冷凝器", valid ? (Sample!.Fan == true ? "风机运行" : "风机停止") : "状态未知", valid && Sample!.Fan == true, false);
        Device(new(324, 179, 154, 70), "节流元件", "循环示意", false, false);
        Device(new(47, 179, 142, 70), "蒸发器", valid ? $"{Sample!.Temperature:0.0} °C" : "— °C", false, false);
        Theme.Text(g, valid ? $"排气 {Sample!.DischargePressure:0.000} MPa" : "排气 — MPa", 9, high ? Theme.Red : Theme.Ink, new(188, 27, 141, 26), alignment: StringAlignment.Center);
        Theme.Text(g, valid ? $"吸气 {Sample!.SuctionPressure:0.000} MPa" : "吸气 — MPa", 9, Theme.Ink, new(185, 225, 147, 24), alignment: StringAlignment.Center);
        Theme.Text(g, "PT-02", 8, Theme.Muted, new(218, 77, 87, 23), alignment: StringAlignment.Center);
        Theme.Text(g, "PT-01", 8, Theme.Muted, new(218, 184, 87, 23), alignment: StringAlignment.Center);
        Theme.Text(g, "设备状态来自采集点 · 保护与启停由 PLC 执行", 8, Theme.Muted, new(15, 256, 490, 20), alignment: StringAlignment.Center);
        g.Restore(state);
        void Device(RectangleF r, string title, string status, bool running, bool alarm)
        {
            using var brush = new SolidBrush(Color.FromArgb(245, 247, 250));
            using var border = new Pen(alarm ? Theme.Red : Color.FromArgb(190, 202, 217), 1.5f);
            g.FillRectangle(brush, r); g.DrawRectangle(border, r.X, r.Y, r.Width, r.Height);
            Theme.Text(g, title, 10, Theme.Ink, new(r.X + 12, r.Y + 9, r.Width - 24, 23), true);
            Theme.Text(g, status, 9, alarm ? Theme.Red : running ? Theme.Green : Theme.Muted, new(r.X + 12, r.Y + 37, r.Width - 24, 23));
            using var dot = new SolidBrush(alarm ? Theme.Red : running ? Theme.Green : Color.FromArgb(170, 179, 190));
            g.FillEllipse(dot, r.Right - 22, r.Y + 17, 7, 7);
        }
    }
}
