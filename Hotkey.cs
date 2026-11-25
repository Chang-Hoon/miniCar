namespace miniCar;

/// <summary>RegisterHotKey의 MOD_* 값과 동일</summary>
[Flags]
internal enum Mod : uint
{
    None = 0,
    Alt = 0x1,
    Ctrl = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>"Ctrl+Alt+Shift+P" 같은 문자열로 표현되는 단축키</summary>
internal readonly record struct Hotkey(Keys Key, Mod Modifiers)
{
    private static readonly Dictionary<string, Keys> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Esc"] = Keys.Escape,
        ["PgUp"] = Keys.PageUp,
        ["PgDn"] = Keys.PageDown,
        ["PageDown"] = Keys.PageDown,
        ["Del"] = Keys.Delete,
        ["Ins"] = Keys.Insert,
        ["Plus"] = Keys.Oemplus,
        ["="] = Keys.Oemplus,
        ["Minus"] = Keys.OemMinus,
        ["-"] = Keys.OemMinus,
        ["Space"] = Keys.Space,
        ["Enter"] = Keys.Enter,
    };

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var mods = Mod.None;
        Keys? key = null;

        // "Ctrl+-" 처럼 키 자체가 '+'/'-'인 경우를 위해 마지막 토큰은 따로 처리
        var tokens = text!.Split('+').Select(t => t.Trim()).ToArray();
        for (int i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (t.Length == 0)
            {
                if (i == tokens.Length - 1) { key = Keys.Oemplus; continue; }
                continue;
            }
            switch (t.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= Mod.Ctrl; continue;
                case "alt": mods |= Mod.Alt; continue;
                case "shift": mods |= Mod.Shift; continue;
                case "win": case "windows": mods |= Mod.Win; continue;
            }

            if (key != null) return false; // 일반 키는 하나만
            if (Aliases.TryGetValue(t, out var aliased)) key = aliased;
            else if (t.Length == 1 && char.IsDigit(t[0])) key = Keys.D0 + (t[0] - '0');
            else if (Enum.TryParse<Keys>(t, true, out var parsed) && parsed != Keys.None
                     && (parsed & Keys.Modifiers) == 0) key = parsed;
            else return false;
        }

        if (key == null) return false;
        hotkey = new Hotkey(key.Value, mods);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(Mod.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(Mod.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(Mod.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(Mod.Win)) parts.Add("Win");
        parts.Add(Key switch
        {
            Keys.PageUp => "PageUp",
            Keys.PageDown => "PageDown",
            Keys.Oemplus => "Plus",
            Keys.OemMinus => "Minus",
            >= Keys.D0 and <= Keys.D9 => ((int)(Key - Keys.D0)).ToString(),
            _ => Key.ToString(),
        });
        return string.Join("+", parts);
    }
}
