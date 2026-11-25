using System.Text;
using static miniCar.Native;

namespace miniCar;

/// <summary>
/// 전체 화면 앱(게임·동영상·발표) 판정.
/// SHQueryUserNotificationState만 믿으면 바탕 화면이 앞에 있을 때(모든 창을 최소화하거나 바탕 화면 보기)
/// "전체 화면 앱 실행 중"으로 잘못 알려 주는 경우가 있어, 맨 앞 창이 실제로 모니터 전체를 덮는 앱인지 한 번 더 확인한다.
/// </summary>
internal static class FullscreenCheck
{
    // 바탕 화면·작업 표시줄·작업 보기 등 셸 창: 화면을 덮어도 전체 화면 앱이 아님
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland", "XamlExplorerHostIslandWindow", "MultitaskingViewFrame",
        "ForegroundStaging", "Windows.UI.Core.CoreWindow",
    };

    private static readonly uint OwnPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

    /// <summary>
    /// 숨어야 하는 상황인지. mode: Games(게임·발표·잠금만) / All(일반 앱 전체 화면 포함) / Never.
    /// detail: 진단용 설명
    /// </summary>
    public static bool ShouldHide(string mode, out string detail)
    {
        if (mode == "Never") { detail = "HideOnFullscreen=Never → 숨기지 않음"; return false; }
        if (SHQueryUserNotificationState(out int state) != 0)
        {
            detail = "상태 조회 실패 → 숨기지 않음";
            return false;
        }
        switch (state)
        {
            case 1: detail = "state=1 (화면보호기·잠금·사용자 전환) → 숨김"; return true;
            case 4: detail = "state=4 (프레젠테이션 모드) → 숨김"; return true;
            case 2:
            case 3:
            {
                bool covers = ForegroundIsFullscreenApp(out string fg);
                // state 3 = DirectX 전체 화면(게임). state 2 = 일반 앱이 화면을 덮음(브라우저 F11, 동영상 등)
                bool hide = covers && (state == 3 || mode == "All");
                string why = !covers ? "전체 화면 앱 아님" : hide ? "숨김" : "일반 앱 전체 화면 (HideOnFullscreen=Games라 그대로 달림)";
                detail = $"state={state} ({(state == 3 ? "DirectX 전체 화면" : "전체 화면 앱")} 보고), 맨 앞 창: {fg} → {(hide ? "숨김" : "숨기지 않음")}: {why}";
                return hide;
            }
            default:
                detail = $"state={state} → 숨기지 않음";
                return false;
        }
    }

    /// <summary>맨 앞 창이 셸·miniCar가 아닌 앱이고, 자기 모니터 전체를 덮고 있는지</summary>
    private static bool ForegroundIsFullscreenApp(out string desc)
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) { desc = "없음"; return false; }

        var cls = new StringBuilder(256);
        GetClassName(hwnd, cls, cls.Capacity);
        string name = cls.ToString();
        GetWindowThreadProcessId(hwnd, out uint pid);
        desc = $"'{name}'";

        if (ShellClasses.Contains(name)) { desc += " (바탕 화면/셸)"; return false; }
        if (pid == OwnPid) { desc += " (miniCar)"; return false; }
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) { desc += " (보이지 않음)"; return false; }
        if (IsZoomed(hwnd)) { desc += " (최대화한 창)"; return false; }
        if ((GetWindowLong(hwnd, GWL_STYLE) & WS_CAPTION) == WS_CAPTION) { desc += " (제목 표시줄 있는 일반 창)"; return false; }
        if (!GetWindowRect(hwnd, out RECT r)) { desc += " (크기 모름)"; return false; }

        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) { desc += " (모니터 모름)"; return false; }

        var m = mi.rcMonitor;
        bool covers = r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
        desc += $" {r.Right - r.Left}x{r.Bottom - r.Top}, 모니터 {m.Right - m.Left}x{m.Bottom - m.Top}" + (covers ? " (모니터 전체)" : "");
        return covers;
    }
}
