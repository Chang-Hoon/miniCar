using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static miniCar.Native;

namespace miniCar;

/// <summary>
/// 주차 모드: 차 아래에 있는 최상위 창을 방향키로 옮긴다.
/// 창을 화면 밖으로 완전히 밀어내지 못하도록, 창 제목 표시줄 가운데가 항상 어느 모니터 안에 남게 한다.
/// 창이 최대화되어 있으면 옮기는 대신 차 위치에 마우스 휠 메시지를 보내 스크롤한다.
/// </summary>
internal sealed class WindowMover
{
    public enum Result
    {
        Idle,
        Moved,
        /// <summary>최대화된 창을 스크롤함</summary>
        Scrolled,
        /// <summary>더 갈 수 없음 (화면 끝)</summary>
        Blocked,
        NoWindow,
        Maximized,
        AccessDenied,
        NotResponding,
    }

    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow", "TopLevelWindowForOverflowXamlIsland",
    };

    private readonly uint _ownPid = (uint)Process.GetCurrentProcess().Id;
    private IntPtr _target;
    private double _x, _y;
    private int _lastLeft, _lastTop, _width, _height;
    private double _held;
    private double _scrollAcc;

    /// <summary>최대화된 창 스크롤: 누르고 있을 때 최고 초당 휠 칸 수</summary>
    public double ScrollNotchesPerSecond { get; set; } = 14;

    /// <summary>
    /// true(기본): 실제 마우스 휠 입력(SendInput). 커서를 차 위치로 옮겨 휠을 굴리고 원래 자리로 돌려놓는 것을
    /// 한 번의 입력 묶음으로 보낸다. Chrome·PDF 리더 등 대부분의 앱에서 동작.
    /// false: 차 아래 창에 WM_MOUSEWHEEL 메시지를 직접 보냄 (커서는 안 움직이지만 무시하는 앱이 많음).
    /// </summary>
    public bool UseRealInput { get; set; } = true;

    public IntPtr Target => _target;
    public bool HasTarget => _target != IntPtr.Zero && IsWindow(_target);
    public string TargetTitle { get; private set; } = "";

    /// <summary>화면 좌표 p 아래에서 가장 위에 있는 일반 창을 대상으로 잡는다.</summary>
    public bool Acquire(Point p)
    {
        _target = FindWindowAt(p);
        _held = 0;
        if (_target == IntPtr.Zero)
        {
            TargetTitle = "";
            return false;
        }
        TargetTitle = TitleOf(_target);
        SyncRect();
        return true;
    }

    public void Release()
    {
        _target = IntPtr.Zero;
        TargetTitle = "";
        _held = 0;
    }

    /// <param name="dirX">-1/0/1</param>
    /// <param name="dirY">-1/0/1</param>
    /// <param name="speed">물리 px/s</param>
    /// <param name="moved">실제로 움직인 거리 (차를 함께 옮길 때 사용)</param>
    /// <param name="at">차 위치 (스크롤 대상 위치)</param>
    public Result Move(double dt, int dirX, int dirY, double speed, MonitorLayout layout, Point at, out Point moved)
    {
        moved = Point.Empty;
        if (dirX == 0 && dirY == 0)
        {
            _held = 0;
            _scrollAcc = 0;
            return Result.Idle;
        }
        if (!HasTarget) return Result.NoWindow;
        if (IsIconic(_target)) return Result.NoWindow;
        if (IsHungAppWindow(_target)) return Result.NotResponding;
        if (IsZoomed(_target)) return Scroll(dt, dirX, dirY, at);

        // 사용자가 그사이 창을 직접 옮겼으면 현재 위치로 다시 맞춤
        if (!GetWindowRect(_target, out var rc)) return Result.NoWindow;
        if (rc.Left != _lastLeft || rc.Top != _lastTop || rc.Right - rc.Left != _width) SyncRect();

        // 누르고 있을수록 빨라짐 (0.5초에 최고 속도)
        _held += dt;
        double factor = Math.Min(1, 0.3 + _held / 0.5);
        double len = Math.Sqrt(dirX * dirX + dirY * dirY);
        double step = speed * factor * dt;
        double nx = _x + dirX / len * step;
        double ny = _y + dirY / len * step;

        // 제목 표시줄 가운데가 화면 안에 남도록
        bool Ok(double x, double y) => layout.ContainsInAnyBounds(x + _width / 2.0, y + 12);
        if (!Ok(nx, ny))
        {
            if (Ok(nx, _y)) ny = _y;
            else if (Ok(_x, ny)) nx = _x;
            else return Result.Blocked;
        }

        int left = (int)Math.Round(nx), top = (int)Math.Round(ny);
        if (left != _lastLeft || top != _lastTop)
        {
            if (!SetWindowPosChecked(_target, IntPtr.Zero, left, top, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE))
            {
                int err = Marshal.GetLastWin32Error();
                return err == ERROR_ACCESS_DENIED ? Result.AccessDenied : Result.NoWindow;
            }

            // 창이 위치를 거부했을 수도 있으니 실제 위치로 확인
            GetWindowRect(_target, out var after);
            moved = new Point(after.Left - _lastLeft, after.Top - _lastTop);
            if (moved == Point.Empty) return Result.AccessDenied; // 관리자 권한 창 등은 조용히 무시됨
            _lastLeft = after.Left;
            _lastTop = after.Top;
        }
        _x = nx;
        _y = ny;
        return Result.Moved;
    }

    /// <summary>
    /// 최대화된 창 스크롤. 차 아래의 가장 안쪽 자식 창에 WM_MOUSEWHEEL(↑↓) / WM_MOUSEHWHEEL(←→)을 보낸다.
    /// 처음 누를 때 한 칸, 누르고 있으면 점점 빠르게 (초당 5칸 → ScrollNotchesPerSecond).
    /// 휠 메시지를 직접 처리하지 않는 앱(일부 UWP 등)은 반응하지 않을 수 있다.
    /// </summary>
    private Result Scroll(double dt, int dirX, int dirY, Point at)
    {
        bool first = _held == 0;
        _held += dt;
        double rate = Math.Min(ScrollNotchesPerSecond, 5 + _held * 12);
        int notches;
        if (first)
        {
            notches = 1;
            _scrollAcc = 0;
        }
        else
        {
            _scrollAcc += rate * dt;
            notches = (int)_scrollAcc;
            _scrollAcc -= notches;
        }

        if (notches == 0) return Result.Scrolled;
        if (UseRealInput)
        {
            return InjectWheel(at, dirX, dirY, notches) ? Result.Scrolled : Result.AccessDenied;
        }

        IntPtr hwnd = DeepestChildAt(_target, at);
        IntPtr lParam = MakeLParam(at.X, at.Y);
        for (int i = 0; i < notches; i++)
        {
            // 휠 위로 = 양수 → 위로 스크롤 / 수평 휠 양수 → 오른쪽으로 스크롤
            if (dirY != 0 && !PostMessage(hwnd, WM_MOUSEWHEEL, MakeWParam(dirY < 0 ? WHEEL_DELTA : -WHEEL_DELTA), lParam))
                return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED ? Result.AccessDenied : Result.NoWindow;
            if (dirX != 0 && !PostMessage(hwnd, WM_MOUSEHWHEEL, MakeWParam(dirX > 0 ? WHEEL_DELTA : -WHEEL_DELTA), lParam))
                return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED ? Result.AccessDenied : Result.NoWindow;
        }
        return Result.Scrolled;
    }

    /// <summary>
    /// [커서를 차 위치로] → [휠 × n] → [커서 원위치] 를 하나의 SendInput 묶음으로 보낸다.
    /// 묶음 안의 입력은 순서대로 처리되므로 휠은 차 아래 창으로 가고, 커서는 바로 제자리로 돌아온다.
    /// </summary>
    private static bool InjectWheel(Point at, int dirX, int dirY, int notches)
    {
        if (!GetCursorPos(out var original)) return false;

        var list = new List<INPUT> { AbsoluteMove(at.X, at.Y) };
        for (int i = 0; i < notches; i++)
        {
            // 휠 위로 = +120 → 위로 스크롤 / 수평 휠 +120 → 오른쪽으로 스크롤
            if (dirY != 0) list.Add(Wheel(MOUSEEVENTF_WHEEL, dirY < 0 ? WHEEL_DELTA : -WHEEL_DELTA));
            if (dirX != 0) list.Add(Wheel(MOUSEEVENTF_HWHEEL, dirX > 0 ? WHEEL_DELTA : -WHEEL_DELTA));
        }
        list.Add(AbsoluteMove(original.X, original.Y));

        var inputs = list.ToArray();
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    private static INPUT Wheel(uint flag, int delta) => new()
    {
        type = INPUT_MOUSE,
        mi = new MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = flag },
    };

    private static INPUT AbsoluteMove(int x, int y)
    {
        var (nx, ny) = NormalizeToVirtualDesk(x, y);
        return new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK },
        };
    }

    /// <summary>
    /// 화면 물리 좌표 → SendInput 절대 좌표(가상 데스크톱 기준 0~65535).
    /// Windows는 픽셀 = 정규화값 × 폭 / 65536 (내림)으로 되돌리므로, 올림으로 계산해야 정확히 같은 픽셀로 돌아온다.
    /// </summary>
    internal static (int nx, int ny) NormalizeToVirtualDesk(int x, int y, int? left = null, int? top = null, int? width = null, int? height = null)
    {
        long l = left ?? GetSystemMetrics(SM_XVIRTUALSCREEN), t = top ?? GetSystemMetrics(SM_YVIRTUALSCREEN);
        long w = Math.Max(1, width ?? GetSystemMetrics(SM_CXVIRTUALSCREEN)), h = Math.Max(1, height ?? GetSystemMetrics(SM_CYVIRTUALSCREEN));
        long nx = ((x - l) * 65536 + w - 1) / w;
        long ny = ((y - t) * 65536 + h - 1) / h;
        return ((int)Math.Max(0, Math.Min(65535, nx)), (int)Math.Max(0, Math.Min(65535, ny)));
    }

    /// <summary>화면 좌표 아래에 있는 가장 안쪽의 보이는 자식 창</summary>
    private static IntPtr DeepestChildAt(IntPtr top, Point screen)
    {
        IntPtr cur = top;
        for (int depth = 0; depth < 32; depth++)
        {
            var pt = new POINT(screen.X, screen.Y);
            if (!ScreenToClient(cur, ref pt)) break;
            IntPtr child = ChildWindowFromPointEx(cur, pt, CWP_SKIPINVISIBLE | CWP_SKIPDISABLED | CWP_SKIPTRANSPARENT);
            if (child == IntPtr.Zero || child == cur) break;
            cur = child;
        }
        return cur;
    }

    /// <summary>wParam: 상위 16비트 = 휠 이동량(부호 있음), 하위 16비트 = 눌린 키(없음)</summary>
    private static IntPtr MakeWParam(int delta) => new IntPtr((int)((uint)(ushort)(short)delta << 16));

    /// <summary>lParam: 화면 좌표 x, y (각각 부호 있는 16비트, 왼쪽/위 모니터의 음수 좌표 포함)</summary>
    private static IntPtr MakeLParam(int x, int y) => new IntPtr((int)(((uint)(ushort)(short)y << 16) | (ushort)(short)x));

    private void SyncRect()
    {
        if (!GetWindowRect(_target, out var rc)) return;
        _x = _lastLeft = rc.Left;
        _y = _lastTop = rc.Top;
        _width = rc.Right - rc.Left;
        _height = rc.Bottom - rc.Top;
    }

    private IntPtr FindWindowAt(Point p)
    {
        IntPtr found = IntPtr.Zero;
        var cls = new StringBuilder(256);
        // EnumWindows는 위에 있는 창부터 차례로 돌려준다 (Z 순서)
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd) || IsIconic(hWnd)) return true;
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == _ownPid) return true;
            if (!GetWindowRect(hWnd, out var r)) return true;
            if (p.X < r.Left || p.X >= r.Right || p.Y < r.Top || p.Y >= r.Bottom) return true;
            if (r.Right - r.Left < 40 || r.Bottom - r.Top < 20) return true;
            if ((GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0) return true; // 클릭 통과 오버레이
            if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            cls.Clear();
            GetClassName(hWnd, cls, cls.Capacity);
            if (IgnoredClasses.Contains(cls.ToString())) return true;

            found = hWnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static string TitleOf(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hWnd, sb, sb.Capacity);
        var t = sb.ToString().Trim();
        if (t.Length == 0) t = "(제목 없음)";
        return t.Length > 30 ? t.Substring(0, 29) + "…" : t;
    }
}
