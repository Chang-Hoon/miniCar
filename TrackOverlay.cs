using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using static miniCar.Native;

namespace miniCar;

/// <summary>GDI+로 바로 그릴 수 있는 32bpp 미리 곱한 알파 DIB (UpdateLayeredWindow에 그대로 넘김)</summary>
internal sealed class DibCanvas : IDisposable
{
    public readonly IntPtr HBitmap;
    public readonly Bitmap Bitmap;
    public readonly Size Size;

    public DibCanvas(int width, int height)
    {
        Size = new Size(Math.Max(1, width), Math.Max(1, height));
        var bmi = new BITMAPINFOHEADER
        {
            biSize = 40,
            biWidth = Size.Width,
            biHeight = -Size.Height, // 위에서 아래로
            biPlanes = 1,
            biBitCount = 32,
        };
        HBitmap = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (HBitmap == IntPtr.Zero) throw new OutOfMemoryException("트랙 화면 비트맵을 만들지 못했습니다");
        Bitmap = new Bitmap(Size.Width, Size.Height, Size.Width * 4, PixelFormat.Format32bppPArgb, bits);
    }

    public void Dispose()
    {
        Bitmap.Dispose();
        DeleteObject(HBitmap);
    }
}

/// <summary>
/// 모니터 하나를 맡는 트랙 창.
/// 평소: 트랙 선이 있는 부분만 덮는 클릭 통과 창.
/// 편집 중: 모니터 전체를 옅게 덮어 마우스를 받는 창 (왼쪽 끌기 = 그리기, 오른쪽 클릭 = 지우기).
/// </summary>
internal sealed class TrackWindow : LayeredWindow
{
    public Rectangle Area { get; }
    public DibCanvas? Canvas { get; private set; }
    public Point Origin { get; set; }
    public bool HasContent { get; set; }

    public event Action<Point>? LeftDown, LeftMove, LeftUp, RightClick;
    private bool _pressed;

    public TrackWindow(Rectangle area) : base(clickThrough: true)
    {
        Area = area;
        Cursor = Cursors.Cross;
    }

    public void SetClickThrough(bool on)
    {
        if (!IsHandleCreated) return;
        int ex = GetWindowLong(Handle, GWL_EXSTYLE);
        int updated = on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        if (updated != ex) SetWindowLong(Handle, GWL_EXSTYLE, updated);
    }

    public void EnsureCanvas(int w, int h)
    {
        if (Canvas != null && Canvas.Size.Width == w && Canvas.Size.Height == h) return;
        Canvas?.Dispose();
        Canvas = new DibCanvas(w, h);
    }

    public void PresentCanvas()
    {
        if (Canvas != null) Present(Canvas.HBitmap, Canvas.Size, Origin);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && !_pressed)
        {
            _pressed = true;
            Capture = true; // 다른 모니터로 넘어가도 계속 받음
            LeftDown?.Invoke(Cursor.Position);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pressed) LeftMove?.Invoke(Cursor.Position);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && _pressed)
        {
            _pressed = false;
            Capture = false;
            LeftUp?.Invoke(Cursor.Position);
        }
        else if (e.Button == MouseButtons.Right && !_pressed)
        {
            RightClick?.Invoke(Cursor.Position);
        }
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && _pressed)
        {
            _pressed = false;
            LeftUp?.Invoke(Cursor.Position);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Canvas?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// 트랙을 화면에 그리고, 편집 모드에서 마우스로 선을 그리게 한다. 모니터마다 창 하나.
/// </summary>
internal sealed class TrackOverlay : IDisposable
{
    private readonly Track _track;
    private readonly MonitorLayout _layout;
    private readonly List<TrackWindow> _windows = new();
    private readonly System.Windows.Forms.Timer _presentTimer = new() { Interval = 15 };
    private List<PointF>? _live;
    private float _liveWidth;
    private bool _shown = true;

    /// <summary>선 굵기 (화면 배율 100% 기준 px)</summary>
    public float WidthLogical { get; set; } = 8;
    public Color Color { get; set; } = Color.FromArgb(255, 179, 0);
    public bool Editing { get; private set; }

    /// <summary>편집 중 오른쪽 클릭으로 선을 지웠는지 등 알림</summary>
    public Action<string>? Notify;

    private static readonly Color EditTint = Color.FromArgb(46, 24, 70, 140);

    public TrackOverlay(Track track, MonitorLayout layout)
    {
        _track = track;
        _layout = layout;
        _track.Changed += Render;
        _presentTimer.Tick += (_, _) => PresentDirty();
    }

    // ───────────────────── 창 관리 ─────────────────────

    /// <summary>모니터 구성에 맞춰 창을 만들거나 정리</summary>
    public void SyncMonitors()
    {
        var areas = _layout.Monitors.Select(m => m.Bounds).ToList();
        if (areas.Count == _windows.Count && areas.All(a => _windows.Any(w => w.Area == a))) return;

        foreach (var w in _windows) w.Close();
        _windows.Clear();
        foreach (var a in areas)
        {
            var w = new TrackWindow(a);
            w.LeftDown += OnLeftDown;
            w.LeftMove += OnLeftMove;
            w.LeftUp += OnLeftUp;
            w.RightClick += OnRightClick;
            _windows.Add(w);
            if (_shown) w.Show();
        }
        Render();
    }

    public void ShowAll()
    {
        _shown = true;
        foreach (var w in _windows) w.Show();
        Render();
    }

    public void HideAll()
    {
        _shown = false;
        if (Editing) SetEditing(false);
        foreach (var w in _windows) w.Hide();
    }

    public void ReassertTopmost()
    {
        foreach (var w in _windows) w.ReassertTopmost();
    }

    public void SetEditing(bool on)
    {
        if (Editing == on) return;
        Editing = on;
        _live = null;
        foreach (var w in _windows)
        {
            w.SetClickThrough(!on);
            if (on && !w.Visible && _shown) w.Show();
        }
        if (on) _presentTimer.Start(); else _presentTimer.Stop();
        Render();
    }

    // ───────────────────── 그리기 ─────────────────────

    /// <summary>모든 창을 처음부터 다시 그림</summary>
    public void Render()
    {
        if (!_shown) return;
        var bounds = _track.Bounds;
        foreach (var w in _windows)
        {
            Rectangle target;
            if (Editing) target = w.Area;
            else
            {
                var r = Rectangle.Intersect(w.Area, Rectangle.Ceiling(RectangleF.Inflate(bounds, 2, 2)));
                if (bounds.IsEmpty || r.Width <= 0 || r.Height <= 0)
                {
                    if (w.HasContent && w.Canvas != null) w.Present(w.Canvas.HBitmap, w.Canvas.Size, w.Origin, 0);
                    w.HasContent = false;
                    continue;
                }
                target = r;
            }

            // 모니터 전체와 똑같은 크기의 맨 위 창은 Windows가 "전체 화면 앱"으로 볼 수 있으므로 1px 줄임
            if (target == w.Area) target = new Rectangle(target.X, target.Y, target.Width, target.Height - 1);
            w.EnsureCanvas(target.Width, target.Height);
            w.Origin = target.Location;
            using (var g = Graphics.FromImage(w.Canvas!.Bitmap))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.Clear(Editing ? EditTint : Color.Transparent);
                g.CompositingMode = CompositingMode.SourceOver;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TranslateTransform(-target.X, -target.Y);
                foreach (var s in _track.Strokes)
                    if (s.Bounds.IntersectsWith(target)) DrawStroke(g, s.Points, s.Width);
                if (_live != null && _live.Count > 0) DrawStroke(g, _live, _liveWidth);
            }
            w.HasContent = true;
            w.PresentCanvas();
        }
    }

    private readonly HashSet<TrackWindow> _dirty = new();

    private void PresentDirty()
    {
        foreach (var w in _dirty) w.PresentCanvas();
        _dirty.Clear();
    }

    /// <summary>
    /// 두 겹으로 그려서 어떤 바탕에서도 보이게: 어두운 테두리 + 밝은 색 가운데.
    /// </summary>
    internal void DrawStroke(Graphics g, IReadOnlyList<PointF> pts, float width)
    {
        using var dark = new Pen(Color.FromArgb(235, 28, 28, 32), width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var core = new Pen(Color, width * 0.52f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (pts.Count == 1)
        {
            var p = pts[0];
            using var db = new SolidBrush(dark.Color);
            using var cb = new SolidBrush(Color);
            g.FillEllipse(db, p.X - width / 2, p.Y - width / 2, width, width);
            g.FillEllipse(cb, p.X - width * 0.26f, p.Y - width * 0.26f, width * 0.52f, width * 0.52f);
            return;
        }
        var arr = pts.ToArray();
        g.DrawLines(dark, arr);
        g.DrawLines(core, arr);
    }

    /// <summary>그리는 중인 선의 새 구간만 덧그림 (모니터 전체를 다시 그리지 않음)</summary>
    private void DrawLiveSegment(PointF a, PointF b)
    {
        var seg = RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        seg.Inflate(_liveWidth, _liveWidth);
        PointF? prev = _live!.Count >= 3 ? _live[_live.Count - 3] : null;
        foreach (var w in _windows)
        {
            if (w.Canvas == null || !seg.IntersectsWith(w.Area)) continue;
            using (var g = Graphics.FromImage(w.Canvas.Bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TranslateTransform(-w.Origin.X, -w.Origin.Y);
                using var dark = new Pen(Color.FromArgb(235, 28, 28, 32), _liveWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                using var core = new Pen(Color, _liveWidth * 0.52f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(dark, a, b);
                if (prev is { } p) g.DrawLine(core, p, a); // 이음매의 어두운 자국을 덮음
                g.DrawLine(core, a, b);
            }
            _dirty.Add(w);
        }
    }

    // ───────────────────── 편집 입력 ─────────────────────

    private int DpiAt(Point p) => _layout.MonitorAt(p.X, p.Y).Dpi;

    private void OnLeftDown(Point p)
    {
        if (!Editing) return;
        _liveWidth = (float)(WidthLogical * DpiAt(p) / 96.0);
        _live = new List<PointF> { p };
        DrawLiveSegment(p, p);
    }

    private void OnLeftMove(Point p)
    {
        if (!Editing || _live == null) return;
        var last = _live[_live.Count - 1];
        double dx = p.X - last.X, dy = p.Y - last.Y;
        if (dx * dx + dy * dy < 9) return; // 3px 이상 움직였을 때만 점 추가
        _live.Add(p);
        DrawLiveSegment(last, p);
    }

    private void OnLeftUp(Point p)
    {
        if (_live == null) return;
        var pts = _live;
        _live = null;
        if (!Editing) return;
        if (pts.Count == 1 || (pts.Count == 2 && Dist(pts[0], pts[1]) < 3))
        {
            Render(); // 거의 안 움직인 클릭은 선으로 치지 않음
            return;
        }
        _track.Add(new TrackStroke(Track.Simplify(pts, 1.0), _liveWidth)); // Changed → Render
    }

    private void OnRightClick(Point p)
    {
        if (!Editing || _live != null) return;
        double tol = 10 * DpiAt(p) / 96.0;
        if (_track.RemoveNear(p.X, p.Y, tol)) Notify?.Invoke("선 하나를 지웠습니다");
    }

    private static double Dist(PointF a, PointF b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public void Dispose()
    {
        _presentTimer.Dispose();
        _track.Changed -= Render;
        foreach (var w in _windows) w.Close();
        _windows.Clear();
    }
}
