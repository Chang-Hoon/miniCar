using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace miniCar;

/// <summary>차 등화 상태 (스프라이트 캐시 구분용)</summary>
internal enum CarLights
{
    Normal,
    Brake,
    /// <summary>비상등 켜짐 (주차 중 깜빡임의 켜진 상태)</summary>
    Hazard,
    /// <summary>후진등</summary>
    Reverse,
    /// <summary>부스트 배기 불꽃 세기 1~4 (부스트 단계 1~2 / 3~4 / 5~7 / 8~10)</summary>
    Boost1,
    Boost2,
    Boost3,
    Boost4,
}

/// <summary>차 디자인</summary>
internal enum CarDesign
{
    /// <summary>오리지널 그랜드 투어러: 긴 보닛, 뒤로 물린 캐빈, 검은 루프, 슬림 LED</summary>
    GT,
    /// <summary>첫 버전 디자인: 둥근 차체 + 흰색 레이싱 스트라이프</summary>
    Classic,
}

/// <summary>
/// 위에서 본 스포츠카를 GDI+로 그린다.
/// 설계 좌표: 길이 48 × 폭 22, 원점 = 차 중심, 앞(코)이 +X 방향.
/// 회전 각도별 HBITMAP을 캐시해서 매 프레임 다시 그리지 않는다.
/// </summary>
internal sealed class CarSprite : IDisposable
{
    public const int AngleStep = 2;                  // 2° 단위 → 180 프레임
    private const int Frames = 360 / AngleStep;
    private const float DesignLength = 48f;
    private const float DesignWidth = 22f;
    private const float DesignCanvas = 96f;          // 회전 대각선 + 그림자 + 부스트 불꽃(최대 4단계) + 여백

    /// <summary>트레이 메뉴의 색상 프리셋</summary>
    public static readonly (string Name, string Hex)[] ColorPresets =
    {
        ("레드", "#E53935"),
        ("실버 블루", "#8DA2B8"),
        ("딥 블루", "#1F3F9E"),
        ("옐로", "#F2C230"),
        ("그린", "#2E8B57"),
        ("오렌지", "#F2711C"),
        ("화이트", "#ECEFF1"),
        ("블랙", "#2B2D31"),
    };

    private readonly Dictionary<(int dpi, int idx, CarLights lights, bool head), IntPtr> _cache = new();
    private Color _color;
    private float _lengthPx;
    private CarDesign _design;

    public CarSprite(Color color, float lengthPx, CarDesign design = CarDesign.GT)
    {
        _color = color;
        _lengthPx = lengthPx;
        _design = design;
    }

    public void Reconfigure(Color color, float lengthPx, CarDesign design)
    {
        if (color.ToArgb() == _color.ToArgb() && Math.Abs(lengthPx - _lengthPx) < 0.01f && design == _design) return;
        ClearCache();
        _color = color;
        _lengthPx = lengthPx;
        _design = design;
    }

    /// <summary>설계 좌표 → 물리 픽셀 배율</summary>
    public float ScaleFor(int dpi) => _lengthPx / DesignLength * dpi / 96f;

    /// <summary>물리 픽셀 기준 차의 반길이/반폭 (충돌 판정용)</summary>
    public (double halfLength, double halfWidth) HalfExtents(int dpi)
    {
        float k = ScaleFor(dpi);
        return (DesignLength / 2 * k, (DesignWidth / 2 + 1.5) * k);
    }

    public IntPtr Get(int dpi, double angleDeg, CarLights lights, out int side) => Get(dpi, angleDeg, lights, false, out side);

    /// <param name="headlights">헤드라이트 켜짐 (램프가 밝게 빛남. 앞쪽 불빛은 BeamWindow가 따로 그림)</param>
    public IntPtr Get(int dpi, double angleDeg, CarLights lights, bool headlights, out int side)
    {
        float k = ScaleFor(dpi);
        side = (int)Math.Ceiling(DesignCanvas * k);
        if (side % 2 == 1) side++;

        int idx = (int)Math.Round(angleDeg / AngleStep) % Frames;
        if (idx < 0) idx += Frames;

        if (!_cache.TryGetValue((dpi, idx, lights, headlights), out var hBitmap))
        {
            using var bmp = Render(k, idx * AngleStep, side, lights: lights, headlights: headlights);
            hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            _cache[(dpi, idx, lights, headlights)] = hBitmap;
        }
        return hBitmap;
    }

    /// <summary>미리보기/테스트용 비트맵</summary>
    internal Bitmap RenderPreview(int dpi, float angle, CarLights lights = CarLights.Normal, bool headlights = false)
    {
        float k = ScaleFor(dpi);
        int side = (int)Math.Ceiling(DesignCanvas * k);
        return Render(k, angle, side, lights: lights, headlights: headlights);
    }

    /// <summary>트레이 아이콘용 (위를 향한 차)</summary>
    public Bitmap RenderIcon(int size)
    {
        float k = size / DesignLength * 0.95f;
        return Render(k, -90, size, shadow: false);
    }

    private Bitmap Render(float k, float angle, int side, bool shadow = true, CarLights lights = CarLights.Normal, bool headlights = false)
    {
        var bmp = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.Clear(Color.Transparent);

        float c = side / 2f;

        if (shadow)
        {
            // 그림자는 화면 기준으로 항상 오른쪽 아래 (차와 함께 돌지 않음)
            var state = g.Save();
            g.TranslateTransform(c + 1.5f * k, c + 2.5f * k);
            g.RotateTransform(angle);
            g.ScaleTransform(k, k);
            using var shadowPath = _design == CarDesign.GT ? GtBodyPath(0.6f) : RoundedRect(new RectangleF(-24.5f, -12f, 49f, 24f), 6f, 10f);
            using var shadowBrush = new SolidBrush(Color.FromArgb(70, 0, 0, 0));
            g.FillPath(shadowBrush, shadowPath);
            g.Restore(state);
        }

        g.TranslateTransform(c, c);
        g.RotateTransform(angle);
        g.ScaleTransform(k, k);

        DrawBoost(g, lights); // 불꽃은 차체 아래로 깔리도록 먼저
        if (_design == CarDesign.GT) DrawGt(g, lights);
        else DrawClassic(g, lights);
        if (headlights) DrawHeadlampGlow(g);
        return bmp;
    }

    // ───────────────────── GT 디자인 ─────────────────────

    /// <summary>
    /// GT 차체 윤곽: 뒤 펜더(허리)가 가장 넓고 문 쪽이 살짝 잘록, 앞 펜더를 지나 코가 좁아지는 형태.
    /// </summary>
    private static GraphicsPath GtBodyPath(float grow = 0f)
    {
        float G(float v) => v + Math.Sign(v) * grow;
        var p = new GraphicsPath();
        // 위쪽(차의 왼쪽) 절반: 뒤 → 앞
        p.AddBezier(G(-23.4f), G(-7.5f), G(-23.6f), G(-10.4f), G(-21.5f), G(-11f), G(-18.5f), G(-11f));
        p.AddLine(G(-18.5f), G(-11f), G(-9f), G(-11f));
        p.AddBezier(G(-9f), G(-11f), G(-5f), G(-11f), G(-2.5f), G(-10.1f), G(1.5f), G(-10.1f));
        p.AddBezier(G(1.5f), G(-10.1f), G(6f), G(-10.1f), G(8.5f), G(-10.8f), G(13f), G(-10.8f));
        p.AddBezier(G(13f), G(-10.8f), G(18f), G(-10.8f), G(21.5f), G(-9.6f), G(23f), G(-6.8f));
        p.AddBezier(G(23f), G(-6.8f), G(24.2f), G(-4.5f), G(24.2f), G(-2f), G(24.2f), 0f);
        // 아래쪽(차의 오른쪽) 절반: 앞 → 뒤
        p.AddBezier(G(24.2f), 0f, G(24.2f), G(2f), G(24.2f), G(4.5f), G(23f), G(6.8f));
        p.AddBezier(G(23f), G(6.8f), G(21.5f), G(9.6f), G(18f), G(10.8f), G(13f), G(10.8f));
        p.AddBezier(G(13f), G(10.8f), G(8.5f), G(10.8f), G(6f), G(10.1f), G(1.5f), G(10.1f));
        p.AddBezier(G(1.5f), G(10.1f), G(-2.5f), G(10.1f), G(-5f), G(11f), G(-9f), G(11f));
        p.AddLine(G(-9f), G(11f), G(-18.5f), G(11f));
        p.AddBezier(G(-18.5f), G(11f), G(-21.5f), G(11f), G(-23.6f), G(10.4f), G(-23.4f), G(7.5f));
        p.CloseFigure();
        return p;
    }

    private void DrawGt(Graphics g, CarLights lights)
    {
        var bounds = new RectangleF(-24.2f, -11f, 48.4f, 22f);

        // ── 타이어 ──
        using (var tire = new SolidBrush(Color.FromArgb(24, 24, 26)))
        {
            foreach (float x in new[] { 13f, -13.5f })
            foreach (float y in new[] { -10.3f, 10.3f })
            {
                using var p = RoundedRect(new RectangleF(x - 4.8f, y - 2.5f, 9.6f, 5f), 1.6f, 1.6f);
                g.FillPath(tire, p);
            }
        }

        // ── 차체 (메탈릭 그라데이션) ──
        using var body = GtBodyPath();
        using (var paint = new LinearGradientBrush(bounds, _color, _color, LinearGradientMode.Vertical))
        {
            paint.InterpolationColors = new ColorBlend
            {
                Colors = new[] { Shade(_color, -0.45f), Shade(_color, 0.05f), Shade(_color, 0.22f), Shade(_color, -0.05f), Shade(_color, -0.45f) },
                Positions = new[] { 0f, 0.22f, 0.42f, 0.7f, 1f },
            };
            g.FillPath(paint, body);
        }

        g.SetClip(body);
        // 빛 반사 띠 (왼쪽 위에서 오는 빛)
        using (var sheen = new LinearGradientBrush(new RectangleF(-24f, -8f, 48f, 6f),
                   Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
        {
            sheen.InterpolationColors = new ColorBlend
            {
                Colors = new[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) },
                Positions = new[] { 0f, 0.5f, 1f },
            };
            g.FillRectangle(sheen, -24f, -8f, 48f, 6f);
        }

        // 보닛 가운데 불룩한 부분 (파워 돔)
        using (var dome = RoundedRect(new RectangleF(5f, -3.4f, 15.5f, 6.8f), 2f, 3.4f))
        using (var domeBrush = new LinearGradientBrush(new RectangleF(5f, -3.4f, 15.5f, 6.8f),
                   Color.FromArgb(55, 255, 255, 255), Color.FromArgb(45, 0, 0, 0), LinearGradientMode.Vertical))
        {
            g.FillPath(domeBrush, dome);
        }

        // 보닛 통풍구 (양쪽 3줄)
        using (var vent = new SolidBrush(Color.FromArgb(150, 15, 15, 18)))
        {
            foreach (float side in new[] { -1f, 1f })
            for (int i = 0; i < 3; i++)
                g.FillRectangle(vent, 12.5f + i * 1.6f, side * 6.6f - 0.8f, 1.1f, 1.6f);
        }

        // 도어 라인
        using (var seam = new Pen(Color.FromArgb(70, 0, 0, 0), 0.3f))
        {
            g.DrawLine(seam, 3.2f, -10.1f, 3.2f, -8.9f);
            g.DrawLine(seam, 3.2f, 10.1f, 3.2f, 8.9f);
            g.DrawLine(seam, -7.5f, -10.9f, -7.5f, -8.6f);
            g.DrawLine(seam, -7.5f, 10.9f, -7.5f, 8.6f);
        }
        g.ResetClip();

        using (var outline = new Pen(Shade(_color, -0.6f), 0.7f))
            g.DrawPath(outline, body);

        // ── 캐빈 (뒤로 물린 위치) ──
        var glassTop = Color.FromArgb(78, 104, 134);
        var glassBottom = Color.FromArgb(20, 28, 40);
        using (var glass = new LinearGradientBrush(new RectangleF(-18f, -9f, 22f, 18f), glassTop, glassBottom, LinearGradientMode.ForwardDiagonal))
        {
            // 앞유리: 보닛 쪽으로 둥글게 휜 형태
            using (var ws = new GraphicsPath())
            {
                ws.AddBezier(2.6f, -8.6f, 4.0f, -4f, 4.0f, 4f, 2.6f, 8.6f);
                ws.AddLine(2.6f, 8.6f, -3.2f, 7.4f);
                ws.AddBezier(-3.2f, 7.4f, -2.4f, 3f, -2.4f, -3f, -3.2f, -7.4f);
                ws.CloseFigure();
                g.FillPath(glass, ws);
            }
            // 옆유리
            g.FillPolygon(glass, new[] { new PointF(-3.2f, -7.4f), new PointF(-14f, -7.1f), new PointF(-13.4f, -8.5f), new PointF(-2.8f, -8.9f) });
            g.FillPolygon(glass, new[] { new PointF(-3.2f, 7.4f), new PointF(-14f, 7.1f), new PointF(-13.4f, 8.5f), new PointF(-2.8f, 8.9f) });
            // 뒷유리
            using (var rw = new GraphicsPath())
            {
                rw.AddLine(-14f, -7.1f, -17.8f, -5.6f);
                rw.AddBezier(-17.8f, -5.6f, -18.4f, -2f, -18.4f, 2f, -17.8f, 5.6f);
                rw.AddLine(-17.8f, 5.6f, -14f, 7.1f);
                rw.CloseFigure();
                g.FillPath(glass, rw);
            }
        }

        // 검은 유광 루프
        using (var roof = RoundedRect(new RectangleF(-14f, -7.1f, 10.8f, 14.2f), 2.2f, 1.6f))
        using (var roofBrush = new LinearGradientBrush(new RectangleF(-14f, -7.1f, 10.8f, 14.2f),
                   Color.FromArgb(58, 62, 70), Color.FromArgb(18, 19, 22), LinearGradientMode.ForwardDiagonal))
        {
            g.FillPath(roofBrush, roof);
        }
        using (var hi = new Pen(Color.FromArgb(120, 255, 255, 255), 0.6f))
        {
            g.DrawLine(hi, 1.8f, -6.2f, 0.4f, -3.2f);   // 앞유리 반사
            g.DrawLine(hi, -12.5f, -5.5f, -6f, -5.8f);  // 루프 반사
        }

        // 뒤 스포일러 립
        using (var lip = new Pen(Color.FromArgb(160, 15, 15, 18), 0.9f))
            g.DrawLine(lip, -21.6f, -8.2f, -21.6f, 8.2f);

        // ── 사이드 미러 ──
        using (var mirror = new SolidBrush(Color.FromArgb(30, 31, 35)))
        {
            g.FillEllipse(mirror, 0.5f, -12.4f, 3.2f, 2.2f);
            g.FillEllipse(mirror, 0.5f, 10.2f, 3.2f, 2.2f);
        }

        // ── 슬림 LED 헤드라이트 (뒤로 쓸린 사선) ──
        using (var led = new SolidBrush(Color.FromArgb(235, 245, 255)))
        {
            g.FillPolygon(led, new[] { new PointF(20.2f, -9.4f), new PointF(23.2f, -6.4f), new PointF(22.6f, -5.6f), new PointF(19.4f, -8.6f) });
            g.FillPolygon(led, new[] { new PointF(20.2f, 9.4f), new PointF(23.2f, 6.4f), new PointF(22.6f, 5.6f), new PointF(19.4f, 8.6f) });
        }

        // ── 배기구 (가운데 두 개) ──
        using (var pipe = new SolidBrush(Color.FromArgb(70, 72, 78)))
        {
            g.FillEllipse(pipe, -24.6f, -4.2f, 1.6f, 1.8f);
            g.FillEllipse(pipe, -24.6f, 2.4f, 1.6f, 1.8f);
        }

        // ── 테일램프 (ㄱ자 모양 두 개) ──
        bool braking = lights == CarLights.Brake;
        if (braking)
        {
            Glow(g, -26f, -7.6f, 10f, Color.FromArgb(170, 255, 40, 30));
            Glow(g, -26f, 7.6f, 10f, Color.FromArgb(170, 255, 40, 30));
        }
        using (var tail = new SolidBrush(braking ? Color.FromArgb(255, 70, 60) : Color.FromArgb(200, 18, 22)))
        {
            g.FillRectangle(tail, -23.6f, -9.6f, 1.2f, 4.2f);
            g.FillRectangle(tail, -23.6f, -9.6f, 2.8f, 1.0f);
            g.FillRectangle(tail, -23.6f, 5.4f, 1.2f, 4.2f);
            g.FillRectangle(tail, -23.6f, 8.6f, 2.8f, 1.0f);
        }

        DrawCommonLamps(g, lights, hazardFront: 21.2f, hazardRear: -22.4f, hazardY: 9.6f);
    }

    // ───────────────────── Classic 디자인 ─────────────────────

    private void DrawClassic(Graphics g, CarLights lights)
    {
        var body = new RectangleF(-24f, -11f, 48f, 22f);

        using (var tire = new SolidBrush(Color.FromArgb(28, 28, 30)))
        {
            foreach (float x in new[] { 13f, -13f })
            foreach (float y in new[] { -10.2f, 10.2f })
            {
                using var p = RoundedRect(new RectangleF(x - 4.5f, y - 2.4f, 9f, 4.8f), 1.6f, 1.6f);
                g.FillPath(tire, p);
            }
        }

        using var bodyPath = RoundedRect(body, 5f, 9f);
        using (var bodyBrush = new LinearGradientBrush(body, _color, _color, LinearGradientMode.Vertical))
        {
            bodyBrush.InterpolationColors = new ColorBlend
            {
                Colors = new[] { Shade(_color, -0.35f), Shade(_color, 0.18f), Shade(_color, -0.35f) },
                Positions = new[] { 0f, 0.5f, 1f },
            };
            g.FillPath(bodyBrush, bodyPath);
        }

        g.SetClip(bodyPath);
        using (var stripe = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
        {
            g.FillRectangle(stripe, -25f, -3.4f, 50f, 2.2f);
            g.FillRectangle(stripe, -25f, 1.2f, 50f, 2.2f);
        }
        g.ResetClip();

        using (var outline = new Pen(Shade(_color, -0.55f), 0.8f))
            g.DrawPath(outline, bodyPath);

        using var glass = new LinearGradientBrush(new RectangleF(-14f, -9f, 24f, 18f),
            Color.FromArgb(70, 95, 125), Color.FromArgb(25, 35, 50), LinearGradientMode.Horizontal);
        g.FillPolygon(glass, new[] { new PointF(9.5f, -8.6f), new PointF(9.5f, 8.6f), new PointF(3f, 7.4f), new PointF(3f, -7.4f) });
        g.FillPolygon(glass, new[] { new PointF(-8f, -7.2f), new PointF(-8f, 7.2f), new PointF(-13.5f, 6f), new PointF(-13.5f, -6f) });
        g.FillPolygon(glass, new[] { new PointF(3f, -7.4f), new PointF(-8f, -7.2f), new PointF(-7.5f, -8.6f), new PointF(2.2f, -8.9f) });
        g.FillPolygon(glass, new[] { new PointF(3f, 7.4f), new PointF(-8f, 7.2f), new PointF(-7.5f, 8.6f), new PointF(2.2f, 8.9f) });

        using (var hi = new Pen(Color.FromArgb(110, 255, 255, 255), 0.7f))
            g.DrawLine(hi, 8.3f, -5.5f, 5f, -2.5f);

        using (var mirror = new SolidBrush(Shade(_color, -0.25f)))
        {
            g.FillEllipse(mirror, 3.5f, -12.6f, 3f, 2.2f);
            g.FillEllipse(mirror, 3.5f, 10.4f, 3f, 2.2f);
        }

        using (var head = new SolidBrush(Color.FromArgb(255, 252, 215)))
        {
            g.FillEllipse(head, 19.5f, -9.3f, 3.6f, 3.2f);
            g.FillEllipse(head, 19.5f, 6.1f, 3.6f, 3.2f);
        }

        bool braking = lights == CarLights.Brake;
        if (braking)
        {
            Glow(g, -24.5f, -7.2f, 11f, Color.FromArgb(170, 255, 40, 30));
            Glow(g, -24.5f, 7.2f, 11f, Color.FromArgb(170, 255, 40, 30));
        }
        using (var tail = new SolidBrush(braking ? Color.FromArgb(255, 70, 60) : Color.FromArgb(210, 20, 20)))
        {
            g.FillRectangle(tail, -24f, -9.2f, 1.7f, 4f);
            g.FillRectangle(tail, -24f, 5.2f, 1.7f, 4f);
        }

        DrawCommonLamps(g, lights, hazardFront: 21.5f, hazardRear: -22.5f, hazardY: 10.2f);
    }

    // ───────────────────── 공통 등화 ─────────────────────

    private static void DrawCommonLamps(Graphics g, CarLights lights, float hazardFront, float hazardRear, float hazardY)
    {
        if (lights == CarLights.Reverse)
        {
            using var rev = new SolidBrush(Color.FromArgb(250, 250, 245));
            g.FillRectangle(rev, -24f, -1.8f, 1.4f, 1.4f);
            g.FillRectangle(rev, -24f, 0.4f, 1.4f, 1.4f);
            Glow(g, -26.5f, 0f, 9f, Color.FromArgb(120, 255, 255, 240));
        }

        if (lights == CarLights.Hazard)
        {
            var amber = Color.FromArgb(255, 176, 0);
            foreach (var (x, y) in new[] { (hazardFront, -hazardY), (hazardFront, hazardY), (hazardRear, -hazardY), (hazardRear, hazardY) })
            {
                Glow(g, x, y, 10f, Color.FromArgb(190, amber));
                using var lamp = new SolidBrush(amber);
                g.FillEllipse(lamp, x - 1.6f, y - 1.4f, 3.2f, 2.8f);
            }
        }
    }

    /// <summary>헤드라이트 켜짐: 앞 램프 두 개가 밝게 빛남 (두 디자인 공통 위치)</summary>
    private void DrawHeadlampGlow(Graphics g)
    {
        foreach (float y in new[] { -7.6f, 7.6f })
        {
            Glow(g, 22.2f, y, 9f, Color.FromArgb(230, 255, 250, 220));
            Glow(g, 22.2f, y, 4.5f, Color.FromArgb(255, 255, 255, 255));
        }
    }

    /// <summary>헤드라이트 불빛을 그릴 때 쓰는 앞 램프 위치 (설계 좌표)</summary>
    internal static readonly PointF[] HeadlampPositions = { new(22.2f, -7.6f), new(22.2f, 7.6f) };

    /// <summary>부스트 단계별 배기 불꽃 (차 뒤쪽으로 뻗음)</summary>
    private static void DrawBoost(Graphics g, CarLights lights)
    {
        int stage = lights switch { CarLights.Boost1 => 1, CarLights.Boost2 => 2, CarLights.Boost3 => 3, CarLights.Boost4 => 4, _ => 0 };
        if (stage == 0) return;

        float len = 3f + stage * 5f;                   // 8 / 13 / 18 / 23
        float w = 2.2f + stage * 0.7f;
        // 단계가 오를수록 주황 → 파랑 (더 뜨거운 불꽃)
        // 단계가 오를수록 주황 → 빨강 → 파랑 → 보라 (더 뜨거운 불꽃)
        Color outer = stage switch
        {
            1 => Color.FromArgb(200, 255, 140, 20),
            2 => Color.FromArgb(210, 255, 90, 40),
            3 => Color.FromArgb(220, 90, 150, 255),
            _ => Color.FromArgb(230, 190, 90, 255),
        };
        Color core = stage >= 3 ? Color.FromArgb(255, 230, 242, 255) : Color.FromArgb(255, 255, 240, 180);

        foreach (float y in new[] { -3.3f, 3.3f })
        {
            using var flame = new GraphicsPath();
            flame.AddBezier(-23.5f, y - w / 2, -23.5f - len * 0.4f, y - w * 0.7f, -23.5f - len * 0.85f, y - w * 0.2f, -23.5f - len, y);
            flame.AddBezier(-23.5f - len, y, -23.5f - len * 0.85f, y + w * 0.2f, -23.5f - len * 0.4f, y + w * 0.7f, -23.5f, y + w / 2);
            flame.CloseFigure();
            // 불꽃 길이 방향으로: 배기구 쪽은 뜨거운 심, 끝으로 갈수록 바깥 색 → 투명 (단계별 길이가 잘 보이게)
            using var brush = new LinearGradientBrush(new PointF(-23.4f, y), new PointF(-23.5f - len, y), core, Color.FromArgb(0, outer))
            {
                InterpolationColors = new ColorBlend
                {
                    Colors = new[] { core, outer, Color.FromArgb(outer.A / 2, outer), Color.FromArgb(0, outer) },
                    Positions = new[] { 0f, 0.3f, 0.75f, 1f },
                },
            };
            g.FillPath(brush, flame);
            Glow(g, -24.5f - len * 0.3f, y, w * 2.6f, Color.FromArgb(90, outer));
        }
    }

    private static void Glow(Graphics g, float cx, float cy, float size, Color center)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(cx - size / 2, cy - size / 2, size, size);
        using var brush = new PathGradientBrush(path)
        {
            CenterColor = center,
            SurroundColors = new[] { Color.FromArgb(0, center) },
        };
        g.FillPath(brush, path);
    }

    /// <summary>뒤(왼쪽)와 앞(오른쪽) 모서리 반경이 다른 둥근 사각형</summary>
    private static GraphicsPath RoundedRect(RectangleF r, float rearRadius, float frontRadius)
    {
        float rr = Math.Min(rearRadius, Math.Min(r.Width, r.Height) / 2);
        float fr = Math.Min(frontRadius, Math.Min(r.Width, r.Height) / 2);
        var p = new GraphicsPath();
        p.AddArc(r.Right - 2 * fr, r.Top, 2 * fr, 2 * fr, 270, 90);
        p.AddArc(r.Right - 2 * fr, r.Bottom - 2 * fr, 2 * fr, 2 * fr, 0, 90);
        p.AddArc(r.Left, r.Bottom - 2 * rr, 2 * rr, 2 * rr, 90, 90);
        p.AddArc(r.Left, r.Top, 2 * rr, 2 * rr, 180, 90);
        p.CloseFigure();
        return p;
    }

    private static Color Shade(Color c, float amount)
    {
        // amount > 0: 밝게, < 0: 어둡게
        int Ch(int v) => amount >= 0
            ? (int)Math.Round(v + (255 - v) * amount)
            : (int)Math.Round(v * (1 + amount));
        return Color.FromArgb(c.A, MathX.Clamp(Ch(c.R), 0, 255), MathX.Clamp(Ch(c.G), 0, 255), MathX.Clamp(Ch(c.B), 0, 255));
    }

    private void ClearCache()
    {
        foreach (var h in _cache.Values) Native.DeleteObject(h);
        _cache.Clear();
    }

    public void Dispose() => ClearCache();
}
