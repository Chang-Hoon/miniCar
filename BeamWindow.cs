using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace miniCar;

/// <summary>
/// 헤드라이트 불빛. 차 앞쪽으로 뻗는 두 줄기의 빛을 그린다.
/// 차 창에 같이 그리면 반투명한 불빛 부분이 마우스 클릭을 가로채므로, 클릭이 통과하는 별도 창에 그린다.
/// 차보다 아래에 놓이고, 회전 각도별 그림을 캐시한다.
/// </summary>
internal sealed class BeamWindow : LayeredWindow
{
    private const int AngleStep = 3;
    private const int Frames = 360 / AngleStep;
    private const float BeamLength = 62f;                 // 램프에서 앞으로 뻗는 길이 (설계 좌표, 차 길이 48 기준)
    private const float Canvas = 2 * (22f + BeamLength + 6f);

    private readonly Dictionary<(int scaleKey, int idx), IntPtr> _cache = new();
    private bool _visible;
    private IntPtr _last;
    private Size _lastSize;
    private Point _lastPos;

    public BeamWindow() : base(clickThrough: true) { }

    /// <param name="scale">설계 좌표 → 물리 픽셀 배율 (CarSprite.ScaleFor)</param>
    public void ShowBeam(double carX, double carY, double angleDeg, float scale)
    {
        int idx = (int)Math.Round(angleDeg / AngleStep) % Frames;
        if (idx < 0) idx += Frames;
        int key = (int)Math.Round(scale * 1000);
        int side = (int)Math.Ceiling(Canvas * scale);
        if (side % 2 == 1) side++;
        if (!_cache.TryGetValue((key, idx), out var hBitmap))
        {
            using var bmp = Render(scale, idx * AngleStep, side);
            hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            _cache[(key, idx)] = hBitmap;
        }
        _last = hBitmap;
        _lastSize = new Size(side, side);
        _lastPos = new Point((int)Math.Round(carX) - side / 2, (int)Math.Round(carY) - side / 2);
        Present(hBitmap, _lastSize, _lastPos);
        _visible = true;
    }

    public void HideBeam()
    {
        if (!_visible || _last == IntPtr.Zero) return;
        Present(_last, _lastSize, _lastPos, 0);
        _visible = false;
    }

    internal static Bitmap Render(float k, float angle, int side)
    {
        var bmp = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        g.TranslateTransform(side / 2f, side / 2f);
        g.RotateTransform(angle);
        g.ScaleTransform(k, k);

        // 따뜻한 흰빛. 밝은 바탕에서도 보이도록 살짝 노란 기운
        var warm = Color.FromArgb(255, 244, 190);
        float x0 = CarSprite.HeadlampPositions[0].X + 0.5f;
        float x1 = x0 + BeamLength;
        float yLamp = Math.Abs(CarSprite.HeadlampPositions[0].Y);

        // 두 램프에서 나온 빛이 앞으로 갈수록 넓어지며 합쳐지는 모양.
        // 폭이 다른 같은 모양을 여러 겹 옅게 겹쳐서 가장자리가 부드럽게 흐려지도록 한다.
        var layers = new[] { (spread: 1.0f, alpha: 52), (spread: 1.18f, alpha: 36), (spread: 1.36f, alpha: 24), (spread: 1.55f, alpha: 15), (spread: 1.75f, alpha: 8) };
        foreach (var (spread, alpha) in layers)
        {
            float startHalf = yLamp + 1.8f * spread;
            float endHalf = 24f * spread;
            using var beam = new GraphicsPath();
            beam.AddLine(x0, -startHalf, x1 - 10, -endHalf);
            beam.AddBezier(x1 - 10, -endHalf, x1 + 2, -endHalf * 0.75f, x1 + 2, endHalf * 0.75f, x1 - 10, endHalf);
            beam.AddLine(x1 - 10, endHalf, x0, startHalf);
            beam.CloseFigure();
            using var brush = new LinearGradientBrush(new PointF(x0 - 1, 0), new PointF(x1 + 3, 0), Color.FromArgb(alpha, warm), Color.FromArgb(0, warm))
            {
                InterpolationColors = new ColorBlend
                {
                    Colors = new[] { Color.FromArgb(alpha, warm), Color.FromArgb(alpha * 7 / 10, warm), Color.FromArgb(alpha * 3 / 10, warm), Color.FromArgb(0, warm) },
                    Positions = new[] { 0f, 0.35f, 0.7f, 1f },
                },
            };
            g.FillPath(brush, beam);
        }

        // ── 밝은 바탕(흰색 등)에서도 보이도록 ──
        // 1) 호박색 기운: 흰빛만으로는 흰 바탕에 묻히므로, 채도가 있는 노란빛을 옅게 겹친다
        //    (어두운 바탕에서는 따뜻한 색감이 될 뿐)
        var amber = Color.FromArgb(255, 186, 40);
        {
            float startHalf = yLamp + 2.2f, endHalf = 26f;
            using var tint = new GraphicsPath();
            tint.AddLine(x0, -startHalf, x1 - 10, -endHalf);
            tint.AddBezier(x1 - 10, -endHalf, x1 + 2, -endHalf * 0.75f, x1 + 2, endHalf * 0.75f, x1 - 10, endHalf);
            tint.AddLine(x1 - 10, endHalf, x0, startHalf);
            tint.CloseFigure();
            using var tb = new LinearGradientBrush(new PointF(x0 - 1, 0), new PointF(x1 + 3, 0), Color.FromArgb(95, amber), Color.FromArgb(0, amber))
            {
                InterpolationColors = new ColorBlend
                {
                    Colors = new[] { Color.FromArgb(105, amber), Color.FromArgb(75, amber), Color.FromArgb(30, amber), Color.FromArgb(0, amber) },
                    Positions = new[] { 0f, 0.35f, 0.75f, 1f },
                },
            };
            g.FillPath(tb, tint);

            // 2) 가장자리 선: 불빛 양옆에 앞으로 갈수록 사라지는 옅은 갈색 선 → 흰 바탕에서 빛의 모양이 드러남
            foreach (float side2 in new[] { -1f, 1f })
            {
                var a = new PointF(x0 + 1, side2 * startHalf);
                var b = new PointF(x1 - 10, side2 * endHalf);
                using var edge = new LinearGradientBrush(a, b, Color.FromArgb(95, 150, 110, 40), Color.FromArgb(0, 150, 110, 40));
                using var pen = new Pen(edge, 0.9f);
                g.DrawLine(pen, a, b);
            }
        }

        // 램프 바로 앞의 밝은 부분 (두 줄기)
        foreach (var lamp in CarSprite.HeadlampPositions)
        {
            using var hot = new GraphicsPath();
            hot.AddEllipse(lamp.X, lamp.Y - 4.5f, 26f, 9f);
            using var hb = new PathGradientBrush(hot)
            {
                CenterPoint = new PointF(lamp.X + 4f, lamp.Y),
                CenterColor = Color.FromArgb(120, warm),
                SurroundColors = new[] { Color.FromArgb(0, warm) },
            };
            g.FillPath(hb, hot);
        }
        return bmp;
    }

    protected override void Dispose(bool disposing)
    {
        foreach (var h in _cache.Values) Native.DeleteObject(h);
        _cache.Clear();
        base.Dispose(disposing);
    }
}
