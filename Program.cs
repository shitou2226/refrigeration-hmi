namespace RefrigerationHmi;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--self-test")) return SelfTest.RunAsync().GetAwaiter().GetResult();
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "程序异常");
        try
        {
            Application.Run(new MainForm());
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败：{ex.Message}\n请检查 AppData 文件夹写入权限。", "制冷监控");
            return 1;
        }
    }
}
