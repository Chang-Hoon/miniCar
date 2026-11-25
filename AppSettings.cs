using System.Globalization;
using System.Reflection;
using System.Text;
using System.Xml;

namespace miniCar;

/// <summary>miniCar.exe.config 의 &lt;appSettings&gt; 에 저장되는 설정 항목 표시</summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class SettingAttribute : Attribute { }

/// <summary>
/// 설정. miniCar.exe 옆의 miniCar.exe.config &lt;appSettings&gt; 에서 읽고 쓴다.
/// 각 항목의 설명과 기본값은 App.config(빌드 시 miniCar.exe.config로 복사, exe에도 포함)에 주석으로 적혀 있다.
///  - 파일이 없으면 exe에 포함된 기본 파일(설명 포함)을 만든다.
///  - 파일에 없는 항목은 기본값, 잘못된 값은 기본값 + 경고.
///  - 저장할 때는 값이 바뀐 항목의 value 속성만 고치므로 주석과 서식이 유지된다.
/// </summary>
internal sealed class AppSettings
{
    // ── 전역 단축키 ──
    [Setting] public string TogglePause { get; set; } = "Ctrl+Alt+Shift+P";
    [Setting] public string ToggleMode { get; set; } = "Ctrl+Alt+Shift+M";
    [Setting] public string SpeedUp { get; set; } = "Ctrl+Alt+Shift+PageUp";
    [Setting] public string SpeedDown { get; set; } = "Ctrl+Alt+Shift+PageDown";
    [Setting] public string Quit { get; set; } = "Ctrl+Alt+Shift+Q";
    [Setting] public string HideToggle { get; set; } = "Ctrl+Alt+Shift+V";
    [Setting] public string TrackEditToggle { get; set; } = "Ctrl+Alt+Shift+T";

    // ── 수동 주행 중에만 쓰는 키 ──
    [Setting] public string ManualExitKey { get; set; } = "Escape";
    [Setting] public string ParkKey { get; set; } = "P";

    // ── 속도 ──
    [Setting] public int[] SpeedLevels { get; set; } = { 60, 100, 150, 220, 300, 420, 560 };
    [Setting] public int SpeedIndex { get; set; } = 2;

    // ── 자율 주행 ──
    [Setting] public double BounceJitterDegrees { get; set; } = 8;
    [Setting] public bool AutoDrift { get; set; } = true;
    [Setting] public double DriftTurnDegreesPerSecond { get; set; } = 240;
    [Setting] public double DriftSlipDegrees { get; set; } = 35;
    [Setting] public bool SkidMarks { get; set; } = true;
    [Setting] public double SkidMarkFadeSeconds { get; set; } = 2.5;

    // ── 수동 주행 ──
    [Setting] public string ManualControlStyle { get; set; } = "Car";
    [Setting] public double SteeringDegreesPerSecond { get; set; } = 200;
    [Setting] public double ManualAccelSeconds { get; set; } = 1.5;
    [Setting] public double ManualCoastSeconds { get; set; } = 2.5;
    [Setting] public double ManualBrakeSeconds { get; set; } = 0.35;
    [Setting] public double[] ManualBoostMultipliers { get; set; } = (double[])CarController.DefaultBoostMultipliers.Clone();
    [Setting] public double ManualBoostHoldSeconds { get; set; } = 1.0;
    [Setting] public double ManualIdleSeconds { get; set; } = 3;
    [Setting] public bool BoostDrift { get; set; } = true;
    [Setting] public bool LaunchTapBoost { get; set; } = true;
    [Setting] public double LaunchTapSeconds { get; set; } = 0.35;
    [Setting] public bool BrakeSkidMarks { get; set; } = true;
    [Setting] public bool BrakeSlide { get; set; } = true;
    [Setting] public double BrakeSlideDegrees { get; set; } = 75;

    // ── 주차 / 창 이동 ──
    [Setting] public int WindowMoveSpeed { get; set; } = 800;
    [Setting] public double ScrollNotchesPerSecond { get; set; } = 14;
    [Setting] public string ScrollMethod { get; set; } = "Input";
    [Setting] public bool ParkCarFollowsWindow { get; set; } = true;

    // ── 외형 ──
    [Setting] public string CarDesign { get; set; } = "GT";
    [Setting] public string CarColor { get; set; } = "#E53935";
    [Setting] public int CarLength { get; set; } = 48;

    // 트랙
    [Setting] public double TrackWidth { get; set; } = 8;
    [Setting] public string TrackColor { get; set; } = "#FFB300";
    [Setting] public string TrackName { get; set; } = "";

    // ── 동작 ──
    [Setting] public bool UseWorkArea { get; set; } = true;
    [Setting] public bool HideOnFullscreen { get; set; } = true;
    [Setting] public bool ShowKeyGuide { get; set; } = false;
    [Setting] public bool ShowSpeedHud { get; set; } = true;
    [Setting] public bool Headlights { get; set; } = false;
    [Setting] public string HeadlightKey { get; set; } = "H";
    [Setting] public string HeadlightToggle { get; set; } = "Ctrl+Alt+Shift+H";

    private static readonly PropertyInfo[] SettingProps = typeof(AppSettings)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(p => p.GetCustomAttribute<SettingAttribute>() != null)
        .ToArray();

    /// <summary>설정 파일 경로: miniCar.exe 와 같은 폴더의 miniCar.exe.config</summary>
    public static string FilePath => AppDomain.CurrentDomain.SetupInformation.ConfigurationFile
                                     ?? Application.ExecutablePath + ".config";

    // ───────────────────── 읽기 ─────────────────────

    /// <summary>
    /// 예전 이름(ToyCar)의 설정 파일이 같은 폴더에 있으면 설정을 이어받는다.
    /// 값은 그대로 두고 글 속의 옛 이름만 바꿔서 새 파일로 저장 (옛 파일은 지우지 않음).
    /// </summary>
    private static void MigrateOldName(string path)
    {
        try
        {
            string old = Path.Combine(Path.GetDirectoryName(path) ?? ".", "ToyCar.exe.config");
            if (!File.Exists(old)) return;
            string text = File.ReadAllText(old, Encoding.UTF8).Replace("ToyCar", "miniCar");
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        catch (Exception) { /* 못 옮기면 기본 파일로 시작 */ }
    }

    public static AppSettings Load(out string? error) => Load(FilePath, out error);

    internal static AppSettings Load(string path, out string? error)
    {
        error = null;
        var s = new AppSettings();
        var problems = new List<string>();

        try
        {
            if (!File.Exists(path)) MigrateOldName(path);
            if (!File.Exists(path)) WriteTemplate(path);
            else Upgrade(path);

            var doc = new XmlDocument();
            doc.Load(path);
            var adds = doc.SelectNodes("/configuration/appSettings/add");
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (adds != null)
            {
                foreach (XmlElement add in adds)
                {
                    present.Add(add.GetAttribute("key"));
                    string key = add.GetAttribute("key");
                    var prop = SettingProps.FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
                    if (prop == null) continue; // 모르는 항목은 무시 (다른 용도로 쓸 수도 있음)
                    if (TryParse(prop.PropertyType, add.GetAttribute("value"), out var value)) prop.SetValue(s, value);
                    else problems.Add($"{prop.Name}=\"{add.GetAttribute("value")}\"");
                }
            }

        }
        catch (XmlException ex)
        {
            error = $"설정 파일 형식 오류 (줄 {ex.LineNumber}): 기본값을 사용합니다.\n파일을 지우면 다음 실행 때 기본 파일을 다시 만듭니다.";
        }
        catch (Exception ex)
        {
            error = $"설정 파일을 읽지 못해 기본값을 사용합니다: {ex.Message}";
        }

        if (problems.Count > 0)
            error = (error == null ? "" : error + "\n") + "잘못된 값이라 기본값을 사용: " + string.Join(", ", problems);

        s.Normalize();
        return s;
    }

    // ───────────────────── 저장 ─────────────────────

    public void Save() => Save(FilePath);

    /// <summary>값이 달라진 항목만 고쳐 쓴다 (주석·서식 유지). 파일에 없는 항목은 끝에 추가.</summary>
    internal void Save(string path, bool onlyAddMissing = false)
    {
        try
        {
            if (!File.Exists(path)) WriteTemplate(path);

            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.Load(path);
            var root = doc.DocumentElement;
            if (root == null) return;
            var section = root.SelectSingleNode("appSettings") as XmlElement;
            if (section == null)
            {
                section = doc.CreateElement("appSettings");
                root.AppendChild(section);
            }

            bool changed = false;
            foreach (var prop in SettingProps)
            {
                object? current = prop.GetValue(this);
                var add = section.SelectNodes("add")?.Cast<XmlElement>()
                    .FirstOrDefault(e => string.Equals(e.GetAttribute("key"), prop.Name, StringComparison.OrdinalIgnoreCase));
                if (add == null)
                {
                    InsertMissing(doc, section, prop.Name, Format(current));
                    changed = true;
                }
                else if (!onlyAddMissing && (!TryParse(prop.PropertyType, add.GetAttribute("value"), out var inFile) || !SameValue(inFile, current)))
                {
                    add.SetAttribute("value", Format(current));
                    changed = true;
                }
            }

            if (!changed) return;
            using var writer = new XmlTextWriter(path, new UTF8Encoding(false));
            doc.Save(writer);
        }
        catch
        {
            // 저장 실패(읽기 전용 폴더 등)는 치명적이지 않음
        }
    }

    /// <summary>예전 버전의 기본값 → 새 기본값 (사용자가 값을 바꾸지 않았을 때만 교체)</summary>
    private static readonly (string key, string[] oldDefaults, string newValue)[] LegacyDefaults =
    {
        ("ManualBoostMultipliers", new[] { "1.4,1.8,2.3" }, "1.4,1.8,2.3,2.8,3.4,4,4.7,5.4,6.2,7"),
    };

    /// <summary>
    /// 새 버전으로 바꾼 뒤 처음 읽을 때 파일을 최신으로 맞춘다. 직접 바꾼 값은 건드리지 않는다.
    ///  1) 각 항목 바로 위 설명 주석을 새 버전 내용으로
    ///  2) 예전 기본값 그대로인 항목은 새 기본값으로 (LegacyDefaults)
    ///  3) 없는 항목은 설명과 함께 제자리에 기본값으로 추가
    /// </summary>
    internal static bool Upgrade(string path)
    {
        var template = LoadTemplate();
        if (template == null) return false;
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(path);
        var section = doc.DocumentElement?.SelectSingleNode("appSettings") as XmlElement;
        if (section == null) return false;
        bool changed = false;

        var userAdds = section.SelectNodes("add")!.Cast<XmlElement>().ToList();
        foreach (var add in userAdds)
        {
            string key = add.GetAttribute("key");
            var tAdd = template.SelectSingleNode($"/configuration/appSettings/add[@key='{key}']");
            if (tAdd == null) continue;

            // 1) 설명 주석
            var tComment = PrecedingComment(tAdd);
            var uComment = PrecedingComment(add);
            if (tComment != null)
            {
                if (uComment == null)
                {
                    section.InsertBefore(doc.CreateComment(tComment.Value), add);
                    section.InsertBefore(doc.CreateWhitespace("\r\n    "), add);
                    changed = true;
                }
                else if (uComment.Value != tComment.Value)
                {
                    uComment.Value = tComment.Value;
                    changed = true;
                }
            }

            // 2) 예전 기본값
            foreach (var (lk, olds, nv) in LegacyDefaults)
            {
                if (!string.Equals(lk, key, StringComparison.OrdinalIgnoreCase)) continue;
                string v = add.GetAttribute("value").Replace(" ", "");
                if (olds.Contains(v))
                {
                    add.SetAttribute("value", nv);
                    changed = true;
                }
            }
        }

        // 3) 없는 항목
        var present = new HashSet<string>(userAdds.Select(e => e.GetAttribute("key")), StringComparer.OrdinalIgnoreCase);
        var defaults = new AppSettings();
        foreach (var prop in SettingProps.Where(p => !present.Contains(p.Name)))
        {
            InsertMissing(doc, section, prop.Name, Format(prop.GetValue(defaults)));
            changed = true;
        }

        if (changed)
        {
            using var writer = new XmlTextWriter(path, new UTF8Encoding(false));
            doc.Save(writer);
        }
        return changed;
    }

    /// <summary>요소 바로 위(공백 건너뜀)의 주석</summary>
    private static XmlComment? PrecedingComment(XmlNode node)
    {
        for (var n = node.PreviousSibling; n != null; n = n.PreviousSibling)
        {
            if (n is XmlComment c) return c;
            if (n is XmlWhitespace || n is XmlSignificantWhitespace) continue;
            return null;
        }
        return null;
    }

    /// <summary>
    /// 파일에 없는 항목을 추가한다. 기본 파일에서 그 항목 바로 위의 설명 주석을 가져오고,
    /// 기본 파일에서 바로 앞에 있던 항목 뒤에 넣는다 (없으면 appSettings 끝).
    /// </summary>
    private static void InsertMissing(XmlDocument doc, XmlElement section, string key, string value)
    {
        const string indent = "\r\n    ";
        var add = doc.CreateElement("add");
        add.SetAttribute("key", key);
        add.SetAttribute("value", value);

        string? comment = null, previousKey = null;
        var template = LoadTemplate();
        var tAdd = template?.SelectSingleNode($"/configuration/appSettings/add[@key='{key}']");
        if (tAdd != null)
        {
            for (var n = tAdd.PreviousSibling; n != null; n = n.PreviousSibling)
            {
                if (n is XmlComment c) { comment = c.Value; break; }
                if (n is XmlElement) break;
            }
            for (var n = tAdd.PreviousSibling; n != null; n = n.PreviousSibling)
                if (n is XmlElement e && e.Name == "add") { previousKey = e.GetAttribute("key"); break; }
        }

        var nodes = new List<XmlNode> { doc.CreateWhitespace(indent) };
        if (comment != null)
        {
            nodes.Add(doc.CreateComment(comment));
            nodes.Add(doc.CreateWhitespace(indent));
        }
        nodes.Add(add);

        var anchor = previousKey == null ? null : section.SelectNodes("add")?.Cast<XmlElement>()
            .FirstOrDefault(e => string.Equals(e.GetAttribute("key"), previousKey, StringComparison.OrdinalIgnoreCase));
        if (anchor != null)
        {
            XmlNode after = anchor;
            foreach (var n in nodes)
            {
                section.InsertAfter(n, after);
                after = n;
            }
        }
        else
        {
            // appSettings 끝 (닫는 태그 앞 공백 앞에)
            var last = section.LastChild;
            foreach (var n in nodes)
            {
                if (last is XmlWhitespace) section.InsertBefore(n, last);
                else section.AppendChild(n);
            }
        }
    }

    private static XmlDocument? LoadTemplate()
    {
        using var res = typeof(AppSettings).Assembly.GetManifestResourceStream("miniCar.App.config");
        if (res == null) return null;
        var doc = new XmlDocument();
        doc.Load(res);
        return doc;
    }

    /// <summary>exe에 포함된 기본 설정 파일(설명 주석 포함)을 쓴다.</summary>
    internal static void WriteTemplate(string path)
    {
        using var res = typeof(AppSettings).Assembly.GetManifestResourceStream("miniCar.App.config");
        if (res == null) return;
        using var file = File.Create(path);
        res.CopyTo(file);
    }

    // ───────────────────── 값 변환 ─────────────────────

    private static bool TryParse(Type type, string text, out object? value)
    {
        text = text?.Trim() ?? "";
        value = null;
        var inv = CultureInfo.InvariantCulture;
        if (type == typeof(string)) { value = text; return true; }
        if (type == typeof(int))
        {
            bool ok = int.TryParse(text, NumberStyles.Integer, inv, out int v);
            value = v; return ok;
        }
        if (type == typeof(double))
        {
            bool ok = double.TryParse(text, NumberStyles.Float, inv, out double v);
            value = v; return ok;
        }
        if (type == typeof(bool))
        {
            bool ok = bool.TryParse(text, out bool v);
            value = v; return ok;
        }
        if (type == typeof(int[]) || type == typeof(double[]))
        {
            var parts = text.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            if (type == typeof(int[]))
            {
                var arr = new int[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                    if (!int.TryParse(parts[i], NumberStyles.Integer, inv, out arr[i])) return false;
                value = arr;
            }
            else
            {
                var arr = new double[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                    if (!double.TryParse(parts[i], NumberStyles.Float, inv, out arr[i])) return false;
                value = arr;
            }
            return true;
        }
        return false;
    }

    private static string Format(object? value) => value switch
    {
        null => "",
        bool b => b ? "true" : "false",
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        int[] a => string.Join(",", a.Select(v => v.ToString(CultureInfo.InvariantCulture))),
        double[] a => string.Join(",", a.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture))),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static bool SameValue(object? a, object? b) => (a, b) switch
    {
        (int[] x, int[] y) => x.SequenceEqual(y),
        (double[] x, double[] y) => x.Length == y.Length && x.Zip(y, (p, q) => Math.Abs(p - q) < 1e-9).All(t => t),
        (double x, double y) => Math.Abs(x - y) < 1e-9,
        (string x, string y) => x == y,
        _ => Equals(a, b),
    };

    // ───────────────────── 범위 보정 ─────────────────────

    public void Normalize()
    {
        var defaults = new AppSettings();
        if (SpeedLevels is null || SpeedLevels.Length == 0) SpeedLevels = defaults.SpeedLevels;
        SpeedLevels = SpeedLevels.Select(v => MathX.Clamp(v, 5, 5000)).ToArray();
        SpeedIndex = MathX.Clamp(SpeedIndex, 0, SpeedLevels.Length - 1);
        CarLength = MathX.Clamp(CarLength, 16, 300);
        TrackWidth = MathX.Clamp(TrackWidth, 2, 40);
        TrackName ??= "";
        BounceJitterDegrees = MathX.Clamp(BounceJitterDegrees, 0, 30);
        DriftTurnDegreesPerSecond = MathX.Clamp(DriftTurnDegreesPerSecond, 60, 720);
        DriftSlipDegrees = MathX.Clamp(DriftSlipDegrees, 0, 70);
        SkidMarkFadeSeconds = MathX.Clamp(SkidMarkFadeSeconds, 0.2, 30);
        ManualAccelSeconds = MathX.Clamp(ManualAccelSeconds, 0.05, 30);
        ManualCoastSeconds = MathX.Clamp(ManualCoastSeconds, 0.05, 60);
        ManualBrakeSeconds = MathX.Clamp(ManualBrakeSeconds, 0.05, 10);
        if (string.IsNullOrWhiteSpace(TogglePause)) TogglePause = defaults.TogglePause;
        if (string.IsNullOrWhiteSpace(ToggleMode)) ToggleMode = defaults.ToggleMode;
        if (string.IsNullOrWhiteSpace(SpeedUp)) SpeedUp = defaults.SpeedUp;
        if (string.IsNullOrWhiteSpace(SpeedDown)) SpeedDown = defaults.SpeedDown;
        if (string.IsNullOrWhiteSpace(Quit)) Quit = defaults.Quit;
        if (string.IsNullOrWhiteSpace(ManualExitKey)) ManualExitKey = defaults.ManualExitKey;
        if (string.IsNullOrWhiteSpace(ParkKey)) ParkKey = defaults.ParkKey;
        if (string.IsNullOrWhiteSpace(CarColor)) CarColor = defaults.CarColor;
        ManualControlStyle = string.Equals(ManualControlStyle?.Trim(), "TopDown", StringComparison.OrdinalIgnoreCase) ? "TopDown" : "Car";
        SteeringDegreesPerSecond = MathX.Clamp(SteeringDegreesPerSecond, 30, 1080);
        WindowMoveSpeed = MathX.Clamp(WindowMoveSpeed, 50, 5000);
        ManualBoostMultipliers = (ManualBoostMultipliers ?? Array.Empty<double>())
            .Select(v => MathX.Clamp(v, 1.0, 20.0)).Take(10).ToArray();
        ManualBoostHoldSeconds = MathX.Clamp(ManualBoostHoldSeconds, 0.1, 10);
        ManualIdleSeconds = MathX.Clamp(ManualIdleSeconds, 0, 60);
        BrakeSlideDegrees = MathX.Clamp(BrakeSlideDegrees, 10, 90);
        LaunchTapSeconds = MathX.Clamp(LaunchTapSeconds, 0.1, 1.0);
        ScrollNotchesPerSecond = MathX.Clamp(ScrollNotchesPerSecond, 1, 60);
        ScrollMethod = string.Equals(ScrollMethod?.Trim(), "Message", StringComparison.OrdinalIgnoreCase) ? "Message" : "Input";
        CarDesign = string.Equals(CarDesign?.Trim(), "Classic", StringComparison.OrdinalIgnoreCase) ? "Classic" : "GT";
    }

    public miniCar.CarDesign GetCarDesign() => CarDesign == "Classic" ? miniCar.CarDesign.Classic : miniCar.CarDesign.GT;

    public ControlStyle GetControlStyle() => ManualControlStyle == "TopDown" ? ControlStyle.TopDown : ControlStyle.Car;

    public Color GetTrackColor()
    {
        try { return ColorTranslator.FromHtml(TrackColor); }
        catch { return Color.FromArgb(255, 179, 0); }
    }

    public Color GetCarColor()
    {
        try { return ColorTranslator.FromHtml(CarColor); }
        catch { return Color.FromArgb(0xE5, 0x39, 0x35); }
    }
}
