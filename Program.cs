namespace miniCar;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\miniCar.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("miniCar가 이미 실행 중입니다. 트레이 아이콘을 확인하세요.", "miniCar",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // DPI 인식(PerMonitorV2)은 app.manifest / App.config에서 설정
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp());
    }
}
