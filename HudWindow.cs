using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace miniCar;

/// <summary>
/// 차 아래에 항상 붙어 다니는 작은 계기판: 부스트 단계(또는 모드)와 속도 숫자, 속도 막대.
/// 막대의 끝은 "기본 최고 속도 × 최고 부스트 배율"이고, 부스트 단계마다 눈금이 있으며
/// 채워진 부분의 색은 현재 단계의 불꽃 색(기본 초록 → 주황 → 빨강 → 파랑 → 보라)이다.
/// 클릭 통과 창이라 마우스 조작을 방해하지 않는다. 값이 바뀔 때만 다시 그린다.
/// </summary>
internal sealed class HudWindow : LayeredWindow
{
    public readonly record struct State(string Label, int StageColor, double Speed, double ScaleMax, double[] Ticks, string Distance = "");

    private IntPtr _hBitmap;
    private Size _size;
    private string? _key;
    private bool _visible;
    private Point _lastPos;

    public HudWindow() : base(clickThrough: true) { }

    /// <param name="speedLogical">화면 배율 100% 기준 속력 (px/s)</param>
    /// <param name="baseMax">기본 최고 속도 (100% 기준 px/s)</param>
    /// <param name="multipliers">부스트 배율 목록 (눈금·막대 끝 계산)</param>
    public void Tick(string label, int stage, double speedLogical, double baseMax, double[] multipliers,
                     double carX, double carY, double carBelow, Rectangle area, int dpi, string distance = "")
    {
        double top = multipliers.Length > 0 ? multipliers.Max() : 1;
        double scaleMax = Math.Max(1, baseMax * Math.Max(1, top));
        var ticks = new[] { baseMax }.Concat(multipliers.Select(m => baseMax * m)).Where(t => t < scaleMax - 0.5).ToArray();
        var state = new State(label, stage, Math.Max(0, speedLogical), scaleMax, ticks, distance);

        // 막대 픽셀 단위로 바뀔 때만 다시 그림
        string key = $"{label}|{stage}|{(int)Math.Round(speedLogical)}|{(int)scaleMax}|{dpi}|{distance}";
        if (key != _key)
        {
            using var bmp = Render(state, dpi);
            if (_hBitmap != IntPtr.Zero) Native.DeleteObject(_hBitmap);
            _hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            _size = bmp.Size;
            _key = key;
        }

        int x = (int)Math.Round(carX - _size.Width / 2.0);
        int y = (int)Math.Round(carY + carBelow);
        if (y + _size.Height > area.Bottom) y = (int)Math.Round(carY - carBelow - _size.Height); // 아래 공간이 없으면 위로
        x = MathX.Clamp(x, area.Left, Math.Max(area.Left, area.Right - _size.Width));
        y = MathX.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - _size.Height));
        _lastPos = new Point(x, y);
        Present(_hBitmap, _size, _lastPos);
        _visible = true;
    }

    public void HideHud()
    {
        if (!_visible || _hBitmap == IntPtr.Zero) return;
        Present(_hBitmap, _size, _lastPos, 0);
        _visible = false;
    }

    /// <summary>단계 → 색 (불꽃 색과 같은 규칙: 1~2 / 3~4 / 5~7 / 8~10)</summary>
    internal static Color StageColor(int stage) => stage switch
    {
        <= 0 => Color.FromArgb(80, 200, 120),   // 기본: 초록
        <= 2 => Color.FromArgb(255, 150, 40),   // 주황
        <= 4 => Color.FromArgb(255, 80, 60),    // 빨강
        <= 7 => Color.FromArgb(90, 150, 255),   // 파랑
        _ => Color.FromArgb(190, 100, 255),     // 보라
    };

    /// <summary>계기판 크기 (화면 배율 100% 기준 px)</summary>
    internal const int HudWidth = 64, HudHeight = 37;

    internal static Bitmap Render(State st, int dpi)
    {
        float s = dpi / 96f;
        int w = (int)Math.Ceiling(HudWidth * s), h = (int)Math.Ceiling(HudHeight * s);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        // 배경 (어느 바탕에서도 보이도록 반투명 검정 + 얇은 밝은 테두리)
        using (var bg = Rounded(new RectangleF(0.5f, 0.5f, w - 1, h - 1), 5 * s))
        {
            using var fill = new SolidBrush(Color.FromArgb(205, 20, 22, 28));
            using var edge = new Pen(Color.FromArgb(70, 255, 255, 255), 1);
            g.FillPath(fill, bg);
            g.DrawPath(edge, bg);
        }

        var color = StageColor(st.StageColor);
        var fmt = StringFormat.GenericTypographic;
        float pad = 4 * s;

        // 1줄: 왼쪽 상태(짧게), 오른쪽 속도 숫자. 폭이 모자라면 상태 글자를 줄임
        string num = ((int)Math.Round(st.Speed)).ToString();
        using var numFont = new Font("Segoe UI", 9.5f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        var numSize = g.MeasureString(num, numFont, PointF.Empty, fmt);
        float labelRoom = w - 2 * pad - numSize.Width - 2 * s;
        float labelPx = 9f * s;
        Font labelFont = new Font("Malgun Gothic", labelPx, FontStyle.Bold, GraphicsUnit.Pixel);
        while (g.MeasureString(st.Label, labelFont, PointF.Empty, fmt).Width > labelRoom && labelPx > 6.5f * s)
        {
            labelFont.Dispose();
            labelPx -= 0.5f * s;
            labelFont = new Font("Malgun Gothic", labelPx, FontStyle.Bold, GraphicsUnit.Pixel);
        }
        using (labelFont)
        using (var lb = new SolidBrush(color))
            g.DrawString(st.Label, labelFont, lb, pad, 3f * s + (9f * s - labelPx) * 0.6f, fmt);
        g.DrawString(num, numFont, Brushes.White, w - pad - numSize.Width, 2.2f * s, fmt);

        // 2줄: 속도 막대
        var bar = new RectangleF(pad, 16 * s, w - 2 * pad, 5 * s);
        using (var track = Rounded(bar, 2.5f * s))
        using (var tb = new SolidBrush(Color.FromArgb(55, 255, 255, 255)))
            g.FillPath(tb, track);

        float frac = (float)MathX.Clamp(st.Speed / st.ScaleMax, 0, 1);
        if (frac > 0.002f)
        {
            var filled = new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, bar.Width * frac), bar.Height);
            using var fp = Rounded(filled, 2.5f * s);
            using var fb = new LinearGradientBrush(new RectangleF(bar.X - 1, bar.Y, bar.Width + 2, bar.Height),
                StageColor(0), color, LinearGradientMode.Horizontal);
            if (st.StageColor <= 0) fb.LinearColors = new[] { StageColor(0), StageColor(0) };
            g.FillPath(fb, fp);
        }

        // 단계 눈금: 폭이 좁으므로 막대 위아래로 살짝만 (기본 최고 속도 눈금은 조금 길게)
        using var tick = new Pen(Color.FromArgb(120, 255, 255, 255), Math.Max(1, 0.8f * s));
        using var baseTick = new Pen(Color.FromArgb(200, 255, 255, 255), Math.Max(1, 1f * s));
        for (int i = 0; i < st.Ticks.Length; i++)
        {
            float tx = bar.X + bar.Width * (float)(st.Ticks[i] / st.ScaleMax);
            if (i == 0) g.DrawLine(baseTick, tx, bar.Y - 1.5f * s, tx, bar.Bottom + 1.5f * s);
            else g.DrawLine(tick, tx, bar.Y, tx, bar.Bottom);
        }
        // 3줄: 주행거리 (이번 실행에서 달린 거리)
        if (!string.IsNullOrEmpty(st.Distance))
        {
            using var sep = new Pen(Color.FromArgb(45, 255, 255, 255), 1);
            g.DrawLine(sep, pad, 24.5f * s, w - pad, 24.5f * s);
            using var small = new Font("Malgun Gothic", 7.5f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            using var distFont = new Font("Segoe UI", 8.5f * s, FontStyle.Bold, GraphicsUnit.Pixel);
            using var gray = new SolidBrush(Color.FromArgb(170, 255, 255, 255));
            g.DrawString("주행", small, gray, pad, 27.2f * s, fmt);
            var ds = g.MeasureString(st.Distance, distFont, PointF.Empty, fmt);
            g.DrawString(st.Distance, distFont, Brushes.White, w - pad - ds.Width, 26.2f * s, fmt);
        }
        return bmp;
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void Dispose(bool disposing)
    {
        if (_hBitmap != IntPtr.Zero) Native.DeleteObject(_hBitmap);
        _hBitmap = IntPtr.Zero;
        base.Dispose(disposing);
    }
}
