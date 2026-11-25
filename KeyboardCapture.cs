using System.Runtime.InteropServices;
using static miniCar.Native;

namespace miniCar;

/// <summary>
/// 수동 주행 모드에서만 설치되는 저수준 키보드 훅.
/// miniCar가 쓰는 키만 가져가고(삼킴), 나머지 키는 그대로 앱과 Windows로 보낸다.
///
/// 가져가는 키:
///  - 운전 키(방향키, Space): 수식키(Ctrl/Alt/Shift/Win) 없이 눌렀을 때만.
///    단 Space는 <see cref="PassSpace"/>가 true(차가 멈춤/주차 중)이면 새로 누를 때 통과
///  - KeyDown 처리기가 true를 돌려준 키: miniCar 단축키, 주차 키, 수동 주행 종료 키
/// 그대로 보내는 키:
///  - 수식키 자체 (그래서 Win+Tab, Alt+Tab, Ctrl+C 등은 평소처럼 동작)
///  - 수식키와 함께 누른 방향키/Space (Win+← 창 붙이기, Shift+→ 선택 등)
///  - 그 밖의 모든 키
///
/// 누름을 삼킨 키만 뗌도 삼키므로, 키가 눌린 채로 남는 일이 없다.
/// </summary>
internal sealed class KeyboardCapture : IDisposable
{
    private const int VK_SPACE = 0x20, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
    private const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4, VK_RMENU = 0xA5, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    private static readonly int[] ModifierVks =
        { VK_LSHIFT, VK_RSHIFT, VK_LCONTROL, VK_RCONTROL, VK_LMENU, VK_RMENU, VK_LWIN, VK_RWIN };

    private readonly LowLevelKeyboardProc _proc; // GC 수집 방지를 위해 필드로 보관
    private readonly HashSet<int> _down = new();
    private readonly HashSet<int> _swallowed = new();
    private readonly HashSet<int> _latched = new(); // 프레임 사이에 눌렀다 뗀 짧은 탭도 놓치지 않도록
    private IntPtr _hook;

    /// <summary>
    /// 운전 키가 아닌 키를 눌렀을 때 (key, 현재 수식키, 자동 반복 여부).
    /// miniCar가 처리할 키면 true를 돌려준다 → 그 키는 다른 앱으로 가지 않음.
    /// 훅 콜백 안에서 호출되므로 가볍게 처리할 것.
    /// </summary>
    public Func<Keys, Mod, bool, bool>? KeyDown;

    /// <summary>true이면 새로 누른 Space를 가져가지 않고 원래 앱으로 보낸다 (차가 멈춰 있을 때)</summary>
    public bool PassSpace;

    public KeyboardCapture() => _proc = HookProc;

    public bool IsActive => _hook != IntPtr.Zero;

    public bool Start()
    {
        if (IsActive) return true;
        _down.Clear();
        _swallowed.Clear();
        _latched.Clear();
        foreach (var vk in ModifierVks)
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) _down.Add(vk);

        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        return _hook != IntPtr.Zero;
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        _down.Clear();
        _swallowed.Clear();
        _latched.Clear();
    }

    /// <summary>
    /// 이번 프레임의 방향키/Space 입력. 지난 프레임 이후 눌렸다가 이미 떼어진 키도 한 번은 눌린 것으로 본다.
    /// </summary>
    public ManualInput ReadInput()
    {
        if (!IsActive) return default;
        // 삼킨(=운전용으로 받은) 키만 운전 입력으로 본다. 수식키와 함께 눌러 통과시킨 방향키는 제외.
        bool D(int vk) => _swallowed.Contains(vk) || _latched.Contains(vk);
        var input = new ManualInput
        {
            Up = D(VK_UP),
            Down = D(VK_DOWN),
            Left = D(VK_LEFT),
            Right = D(VK_RIGHT),
            Brake = D(VK_SPACE),
        };
        _latched.Clear();
        return input;
    }

    private Mod CurrentModifiers()
    {
        var m = Mod.None;
        if (_down.Contains(VK_LCONTROL) || _down.Contains(VK_RCONTROL) || _down.Contains(VK_CONTROL)) m |= Mod.Ctrl;
        if (_down.Contains(VK_LMENU) || _down.Contains(VK_RMENU) || _down.Contains(VK_MENU)) m |= Mod.Alt;
        if (_down.Contains(VK_LSHIFT) || _down.Contains(VK_RSHIFT) || _down.Contains(VK_SHIFT)) m |= Mod.Shift;
        if (_down.Contains(VK_LWIN) || _down.Contains(VK_RWIN)) m |= Mod.Win;
        return m;
    }

    private static bool IsDrivingKey(int vk) => vk is VK_LEFT or VK_UP or VK_RIGHT or VK_DOWN or VK_SPACE;

    private static bool IsModifier(int vk) =>
        vk is VK_SHIFT or VK_CONTROL or VK_MENU or VK_LSHIFT or VK_RSHIFT or VK_LCONTROL or VK_RCONTROL
            or VK_LMENU or VK_RMENU or VK_LWIN or VK_RWIN;

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || _hook == IntPtr.Zero)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        int vk = (int)info.vkCode;
        int msg = (int)wParam;

        bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool isUp = msg is WM_KEYUP or WM_SYSKEYUP;
        if ((isDown || isUp) && ProcessKey(vk, isDown)) return (IntPtr)1; // 삼킴

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>키 이벤트 하나를 처리하고, 삼켜야 하면 true. (훅과 분리해서 테스트 가능)</summary>
    internal bool ProcessKey(int vk, bool isDown)
    {
        if (!isDown)
        {
            _down.Remove(vk);
            return _swallowed.Remove(vk); // 누름을 삼킨 키만 뗌도 삼킴
        }

        bool repeat = !_down.Add(vk);
        if (IsModifier(vk)) return false; // 수식키는 항상 통과

        if (repeat)
        {
            // 자동 반복: 처음 누름을 삼켰으면 계속 삼키고, 통과시켰으면 계속 통과
            if (!_swallowed.Contains(vk)) return false;
            if (!IsDrivingKey(vk)) Invoke(vk, repeat: true);
            return true;
        }

        var mods = CurrentModifiers();
        if (IsDrivingKey(vk) && mods == Mod.None)
        {
            if (vk == VK_SPACE && PassSpace) return false; // 멈춘 차: 브레이크 불필요 → 타이핑용 Space
            _swallowed.Add(vk);
            _latched.Add(vk);
            return true;
        }

        if (Invoke(vk, repeat: false))
        {
            _swallowed.Add(vk);
            return true;
        }
        return false;
    }

    private bool Invoke(int vk, bool repeat)
    {
        try { return KeyDown?.Invoke((Keys)vk, CurrentModifiers(), repeat) ?? false; }
        catch { return false; } // 훅 콜백에서 예외가 새면 안 됨
    }

    /// <summary>테스트용: 훅 없이 활성 상태로 만든다</summary>
    internal void StartForTest()
    {
        _down.Clear();
        _swallowed.Clear();
        _latched.Clear();
        _hook = new IntPtr(-1);
        PassSpace = false;
    }

    public void Dispose() => Stop();
}
