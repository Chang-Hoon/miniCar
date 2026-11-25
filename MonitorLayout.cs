using System.Runtime.InteropServices;
using System.Text;
using static miniCar.Native;

namespace miniCar;

/// <summary>
/// 연결된 모니터 목록. 좌표는 가상 화면 물리 픽셀(음수 가능).
/// 차가 다닐 수 있는 영역 = 각 모니터 영역(작업 영역)의 합집합 + 이웃 모니터 사이의 "다리".
///
/// 다리가 필요한 경우:
///  - 위아래로 놓인 모니터 사이에 작업표시줄이 끼어 있으면 두 작업 영역이 붙어 있지 않다.
///  - DPI 인식이 제대로 적용되지 않으면(가상화된 좌표) 배율이 다른 모니터 사이에 틈이 생긴다.
/// 마주 보는 두 영역의 간격이 MaxBridgeGap 이하이고 겹치는 구간이 있으면 그 틈을 통과 가능 영역으로 본다.
/// </summary>
internal sealed class MonitorLayout
{
    public readonly record struct Monitor(Rectangle Bounds, Rectangle Work, bool Primary, int Dpi);

    /// <summary>다리로 이어 줄 최대 간격 (물리 px). 작업표시줄 두께(배율 200%에서 약 100px)보다 넉넉하게.</summary>
    public const int MaxBridgeGap = 240;

    private bool _useWorkArea = true;
    private List<Rectangle> _areas = new();
    private List<Rectangle> _bridges = new();

    public IReadOnlyList<Monitor> Monitors { get; private set; } = Array.Empty<Monitor>();
    public IReadOnlyList<Rectangle> Bridges => _bridges;

    public bool UseWorkArea
    {
        get => _useWorkArea;
        set { _useWorkArea = value; Rebuild(); }
    }

    public Rectangle AreaOf(Monitor m) => _useWorkArea ? m.Work : m.Bounds;

    public void Refresh()
    {
        var list = new List<Monitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                int dpi = 96;
                try
                {
                    if (GetDpiForMonitor(hMon, MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 && dx > 0) dpi = (int)dx;
                }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }

                list.Add(new Monitor(ToRect(mi.rcMonitor), ToRect(mi.rcWork), (mi.dwFlags & 1) != 0, dpi));
            }
            return true;
        }, IntPtr.Zero);

        if (list.Count > 0) SetMonitors(list);
    }

    /// <summary>모니터 목록 직접 지정 (테스트에서도 사용)</summary>
    internal void SetMonitors(IReadOnlyList<Monitor> monitors)
    {
        Monitors = monitors;
        Rebuild();
    }

    private void Rebuild()
    {
        _areas = Monitors.Select(AreaOf).ToList();
        _bridges = new List<Rectangle>();

        for (int i = 0; i < _areas.Count; i++)
        for (int j = 0; j < _areas.Count; j++)
        {
            if (i == j) continue;
            var a = _areas[i];
            var b = _areas[j];

            // a의 오른쪽 → b의 왼쪽 (좌우 배치)
            int gapX = b.Left - a.Right;
            if (gapX > 0 && gapX <= MaxBridgeGap)
            {
                int top = Math.Max(a.Top, b.Top), bottom = Math.Min(a.Bottom, b.Bottom);
                if (bottom > top) _bridges.Add(Rectangle.FromLTRB(a.Right, top, b.Left, bottom));
            }

            // a의 아래쪽 → b의 위쪽 (위아래 배치)
            int gapY = b.Top - a.Bottom;
            if (gapY > 0 && gapY <= MaxBridgeGap)
            {
                int left = Math.Max(a.Left, b.Left), right = Math.Min(a.Right, b.Right);
                if (right > left) _bridges.Add(Rectangle.FromLTRB(left, a.Bottom, right, b.Top));
            }
        }

        // 다리가 다른 모니터 영역을 가로지르면 안 됨 (중간에 다른 모니터가 있는 경우 등)
        _bridges.RemoveAll(br => _areas.Any(r => r.IntersectsWith(br)));
    }

    public bool Contains(double x, double y)
    {
        foreach (var r in _areas)
            if (x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom) return true;
        foreach (var r in _bridges)
            if (x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom) return true;
        return false;
    }

    /// <summary>모니터 전체 영역(작업표시줄 포함) 중 어딘가에 있는가</summary>
    public bool ContainsInAnyBounds(double x, double y)
    {
        foreach (var m in Monitors)
        {
            var r = m.Bounds;
            if (x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom) return true;
        }
        return false;
    }

    /// <summary>점이 속한 모니터. 어디에도 없으면 가장 가까운 모니터.</summary>
    public Monitor MonitorAt(double x, double y)
    {
        Monitor best = Primary;
        double bestDist = double.MaxValue;
        foreach (var m in Monitors)
        {
            var r = AreaOf(m);
            double dx = x < r.Left ? r.Left - x : x >= r.Right ? x - r.Right + 1 : 0;
            double dy = y < r.Top ? r.Top - y : y >= r.Bottom ? y - r.Bottom + 1 : 0;
            double d = dx * dx + dy * dy;
            if (d == 0) return m;
            if (d < bestDist) { bestDist = d; best = m; }
        }
        return best;
    }

    public Monitor Primary
    {
        get
        {
            foreach (var m in Monitors) if (m.Primary) return m;
            if (Monitors.Count > 0) return Monitors[0];
            var b = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            return new Monitor(b, b, true, 96);
        }
    }

    /// <summary>문제 진단용 텍스트 (트레이 메뉴 → 모니터 정보 복사)</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"miniCar 모니터 정보 ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
        sb.AppendLine($"프로세스 DPI 인식: {DpiAwarenessName()}");
        sb.AppendLine($"작업 영역 사용(UseWorkArea): {_useWorkArea}");
        int n = 1;
        foreach (var m in Monitors)
        {
            sb.AppendLine($"[{n++}] {(m.Primary ? "주 모니터" : "보조")}  DPI {m.Dpi} ({m.Dpi * 100 / 96}%)");
            sb.AppendLine($"    전체 영역: {Fmt(m.Bounds)}");
            sb.AppendLine($"    작업 영역: {Fmt(m.Work)}");
        }
        if (_bridges.Count == 0) sb.AppendLine("다리: 없음 (모든 영역이 바로 붙어 있거나 이웃이 없음)");
        foreach (var b in _bridges) sb.AppendLine($"다리: {Fmt(b)}");
        return sb.ToString();

        static string Fmt(Rectangle r) => $"({r.Left}, {r.Top}) ~ ({r.Right}, {r.Bottom})  {r.Width}×{r.Height}";
    }

    private static string DpiAwarenessName()
    {
        try
        {
            var ctx = GetThreadDpiAwarenessContext();
            if (AreDpiAwarenessContextsEqual(ctx, new IntPtr(-4))) return "PerMonitorV2 (정상)";
            if (AreDpiAwarenessContextsEqual(ctx, new IntPtr(-3))) return "PerMonitor";
            if (AreDpiAwarenessContextsEqual(ctx, new IntPtr(-2))) return "System (좌표가 가상화됨)";
            if (AreDpiAwarenessContextsEqual(ctx, new IntPtr(-1))) return "Unaware (좌표가 가상화됨)";
            return "알 수 없음";
        }
        catch (EntryPointNotFoundException)
        {
            return "확인 불가 (Windows 10 1607 이전)";
        }
    }

    private static Rectangle ToRect(RECT r) => Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
}
