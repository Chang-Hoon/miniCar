using System.Globalization;
using System.Text;

namespace miniCar;

/// <summary>사용자가 마우스로 그린 트랙 선 하나 (가상 화면 물리 픽셀 좌표, 굵기도 물리 픽셀)</summary>
internal sealed class TrackStroke
{
    public readonly List<PointF> Points;
    public readonly float Width;
    public RectangleF Bounds { get; private set; }

    public TrackStroke(IEnumerable<PointF> points, float width)
    {
        Points = points.ToList();
        Width = width;
        UpdateBounds();
    }

    public void UpdateBounds()
    {
        if (Points.Count == 0) { Bounds = RectangleF.Empty; return; }
        float l = Points.Min(p => p.X), t = Points.Min(p => p.Y), r = Points.Max(p => p.X), b = Points.Max(p => p.Y);
        float h = Width / 2;
        Bounds = RectangleF.FromLTRB(l - h, t - h, r + h, b + h);
    }

    /// <summary>점에서 이 선까지의 거리 (선 중심 기준)와 가장 가까운 점</summary>
    public double Distance(double x, double y, out double cx, out double cy)
    {
        double best = double.MaxValue;
        cx = cy = 0;
        if (Points.Count == 1)
        {
            cx = Points[0].X; cy = Points[0].Y;
            return Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
        }
        for (int i = 0; i + 1 < Points.Count; i++)
        {
            double d = Track.SegmentDistance(x, y, Points[i], Points[i + 1], out double px, out double py);
            if (d < best) { best = d; cx = px; cy = py; }
        }
        return best;
    }
}

/// <summary>
/// 트랙: 차가 벽처럼 부딪히는 선들의 모음.
/// 좌표는 가상 화면 물리 픽셀. 이름이 있으면 tracks 폴더의 "이름.track" 파일과 연결된다.
/// </summary>
internal sealed class Track
{
    public readonly List<TrackStroke> Strokes = new();

    /// <summary>불러오거나 저장한 이름 (null = 아직 저장 안 한 새 트랙)</summary>
    public string? Name { get; set; }
    /// <summary>마지막 저장/불러오기 이후 바뀌었는지</summary>
    public bool Modified { get; set; }
    public bool IsEmpty => Strokes.Count == 0;

    /// <summary>선이 추가·삭제되면 (다시 그리기용)</summary>
    public event Action? Changed;

    public void Add(TrackStroke stroke)
    {
        Strokes.Add(stroke);
        Modified = true;
        Changed?.Invoke();
    }

    public bool RemoveLast()
    {
        if (Strokes.Count == 0) return false;
        Strokes.RemoveAt(Strokes.Count - 1);
        Modified = true;
        Changed?.Invoke();
        return true;
    }

    /// <summary>(x, y)에서 tolerance 안에 있는 가장 가까운 선을 지움</summary>
    public bool RemoveNear(double x, double y, double tolerance)
    {
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < Strokes.Count; i++)
        {
            var s = Strokes[i];
            double d = s.Distance(x, y, out _, out _) - s.Width / 2;
            if (d <= tolerance && d < bestD) { bestD = d; best = i; }
        }
        if (best < 0) return false;
        Strokes.RemoveAt(best);
        Modified = true;
        Changed?.Invoke();
        return true;
    }

    /// <summary>모두 지우고 이름 없는 새 트랙으로</summary>
    public void Clear()
    {
        Strokes.Clear();
        Name = null;
        Modified = false;
        Changed?.Invoke();
    }

    /// <summary>다른 트랙 내용으로 바꿈 (불러오기)</summary>
    public void ReplaceWith(IEnumerable<TrackStroke> strokes, string? name)
    {
        Strokes.Clear();
        Strokes.AddRange(strokes);
        Name = name;
        Modified = false;
        Changed?.Invoke();
    }

    public RectangleF Bounds
    {
        get
        {
            if (Strokes.Count == 0) return RectangleF.Empty;
            var r = Strokes[0].Bounds;
            for (int i = 1; i < Strokes.Count; i++) r = RectangleF.Union(r, Strokes[i].Bounds);
            return r;
        }
    }

    // ───────────────────── 충돌 ─────────────────────

    /// <summary>
    /// 차(회전된 직사각형을 원 3개로 근사)가 트랙 선에 파고든 깊이 (px, 0 = 닿지 않음).
    /// (nx, ny) = 가장 깊이 닿은 곳에서 차를 밀어낼 방향 (단위 벡터). skip에 든 선은 무시.
    /// </summary>
    public double Penetration(double x, double y, double angleDeg, double halfLength, double halfWidth, out double nx, out double ny,
        ICollection<TrackStroke>? skip = null)
    {
        nx = ny = 0;
        double best = 0;
        foreach (var s in Strokes)
        {
            if (skip != null && skip.Contains(s)) continue;
            double pen = StrokePenetration(s, x, y, angleDeg, halfLength, halfWidth, out double sx, out double sy);
            if (pen > best) { best = pen; nx = sx; ny = sy; }
        }
        return best;
    }

    /// <summary>선 하나에 대한 파고든 깊이</summary>
    public static double StrokePenetration(TrackStroke s, double x, double y, double angleDeg, double halfLength, double halfWidth,
        out double nx, out double ny)
    {
        nx = ny = 0;
        double a = angleDeg * Math.PI / 180, fx = Math.Cos(a), fy = Math.Sin(a);
        double r = halfWidth * 0.95;
        double off = Math.Max(0, halfLength - r);
        double reach = halfLength + 2;
        double need = r + s.Width / 2;
        var b = s.Bounds;
        if (x < b.Left - reach || x > b.Right + reach || y < b.Top - reach || y > b.Bottom + reach) return 0;

        double best = 0;
        for (int k = -1; k <= 1; k++)
        {
            double cx = x + fx * off * k, cy = y + fy * off * k;
            if (cx < b.Left - r || cx > b.Right + r || cy < b.Top - r || cy > b.Bottom + r) continue;

            for (int i = 0; i < Math.Max(1, s.Points.Count - 1); i++)
            {
                PointF p0 = s.Points[i], p1 = s.Points.Count > 1 ? s.Points[i + 1] : p0;
                if (cx < Math.Min(p0.X, p1.X) - need || cx > Math.Max(p0.X, p1.X) + need ||
                    cy < Math.Min(p0.Y, p1.Y) - need || cy > Math.Max(p0.Y, p1.Y) + need) continue;

                double d = SegmentDistance(cx, cy, p0, p1, out double px, out double py);
                double pen = need - d;
                if (pen <= best) continue;
                best = pen;
                if (d > 1e-6) { nx = (cx - px) / d; ny = (cy - py) / d; }
                else
                {
                    // 원 중심이 선 위: 선의 수직 방향 중 차 중심 쪽
                    double sx = p1.X - p0.X, sy = p1.Y - p0.Y, len = Math.Sqrt(sx * sx + sy * sy);
                    if (len < 1e-6) { nx = 1; ny = 0; }
                    else
                    {
                        nx = -sy / len; ny = sx / len;
                        if ((x - px) * nx + (y - py) * ny < 0) { nx = -nx; ny = -ny; }
                    }
                }
            }
        }
        return best;
    }

    internal static double SegmentDistance(double x, double y, PointF a, PointF b, out double px, out double py)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        double t = len2 < 1e-9 ? 0 : MathX.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / len2, 0, 1);
        px = a.X + dx * t;
        py = a.Y + dy * t;
        return Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
    }

    // ───────────────────── 선 단순화 ─────────────────────

    /// <summary>마우스로 그린 점들을 모양은 유지하며 줄임 (Douglas-Peucker)</summary>
    public static List<PointF> Simplify(IReadOnlyList<PointF> pts, double tolerance)
    {
        if (pts.Count <= 2) return pts.ToList();
        var keep = new bool[pts.Count];
        keep[0] = keep[pts.Count - 1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (s, e) = stack.Pop();
            double max = 0;
            int idx = -1;
            for (int i = s + 1; i < e; i++)
            {
                double d = SegmentDistance(pts[i].X, pts[i].Y, pts[s], pts[e], out _, out _);
                if (d > max) { max = d; idx = i; }
            }
            if (idx >= 0 && max > tolerance)
            {
                keep[idx] = true;
                stack.Push((s, idx));
                stack.Push((idx, e));
            }
        }
        var result = new List<PointF>();
        for (int i = 0; i < pts.Count; i++) if (keep[i]) result.Add(pts[i]);
        return result;
    }

    // ───────────────────── 파일 ─────────────────────

    private const string Header = "miniCar-track 1";

    /// <summary>
    /// 텍스트 형식:
    ///   miniCar-track 1
    ///   stroke 굵기 x,y x,y ...
    /// </summary>
    public static string Serialize(IEnumerable<TrackStroke> strokes)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');
        var inv = CultureInfo.InvariantCulture;
        foreach (var s in strokes)
        {
            sb.Append("stroke ").Append(s.Width.ToString("0.#", inv));
            foreach (var p in s.Points) sb.Append(' ').Append(p.X.ToString("0.#", inv)).Append(',').Append(p.Y.ToString("0.#", inv));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    public static List<TrackStroke> Parse(string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != Header) throw new FormatException("miniCar 트랙 파일이 아닙니다");
        var inv = CultureInfo.InvariantCulture;
        var result = new List<TrackStroke>();
        for (int i = 1; i < lines.Length; i++)
        {
            var parts = lines[i].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            if (parts[0] != "stroke" || parts.Length < 3) throw new FormatException($"{i + 1}번째 줄 형식 오류");
            float width = float.Parse(parts[1], inv);
            var pts = new List<PointF>();
            for (int j = 2; j < parts.Length; j++)
            {
                var xy = parts[j].Split(',');
                if (xy.Length != 2) throw new FormatException($"{i + 1}번째 줄 좌표 오류");
                pts.Add(new PointF(float.Parse(xy[0], inv), float.Parse(xy[1], inv)));
            }
            result.Add(new TrackStroke(pts, Math.Max(1f, Math.Min(200f, width))));
        }
        return result;
    }
}

/// <summary>저장된 트랙 파일 관리: exe 옆 tracks 폴더의 "이름.track"</summary>
internal sealed class TrackStore
{
    public const string Extension = ".track";
    public string Folder { get; }

    public TrackStore(string folder) => Folder = folder;

    public static TrackStore Default() =>
        new(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath) ?? ".", "tracks"));

    public IReadOnlyList<string> Names()
    {
        if (!Directory.Exists(Folder)) return Array.Empty<string>();
        return Directory.GetFiles(Folder, "*" + Extension)
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>파일 이름으로 쓸 수 있는 이름인지. 안 되면 이유를 돌려줌</summary>
    public static string? Validate(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return "이름을 입력하세요.";
        if (name!.Length > 60) return "이름은 60자까지 쓸 수 있습니다.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
            return "이름에 \\ / : * ? \" < > | 는 쓸 수 없습니다.";
        if (name.EndsWith(".")) return "이름이 마침표로 끝날 수 없습니다.";
        return null;
    }

    public string PathOf(string name) => Path.Combine(Folder, name.Trim() + Extension);
    public bool Exists(string name) => File.Exists(PathOf(name));

    public void Save(string name, IEnumerable<TrackStroke> strokes)
    {
        Directory.CreateDirectory(Folder);
        string path = PathOf(name), tmp = path + ".tmp";
        File.WriteAllText(tmp, Track.Serialize(strokes), new UTF8Encoding(false));
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    public List<TrackStroke> Load(string name) => Track.Parse(File.ReadAllText(PathOf(name), Encoding.UTF8));

    public void Delete(string name)
    {
        string path = PathOf(name);
        if (File.Exists(path)) File.Delete(path);
    }
}
