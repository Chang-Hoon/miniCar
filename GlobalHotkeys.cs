using static miniCar.Native;

namespace miniCar;

/// <summary>RegisterHotKey 기반 전역 단축키 (자율 주행 모드에서 사용)</summary>
internal sealed class GlobalHotkeys : NativeWindow, IDisposable
{
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public GlobalHotkeys()
    {
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE }); // 메시지 전용 창
    }

    /// <returns>다른 프로그램이 이미 사용 중이면 false</returns>
    public bool Register(Hotkey hotkey, Action action, bool allowRepeat)
    {
        int id = _nextId++;
        uint mods = (uint)hotkey.Modifiers | (allowRepeat ? 0u : MOD_NOREPEAT);
        if (!RegisterHotKey(Handle, id, mods, (uint)hotkey.Key)) return false;
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(Handle, id);
        _actions.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && _actions.TryGetValue((int)m.WParam, out var action))
        {
            action();
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterAll();
        DestroyHandle();
    }
}
