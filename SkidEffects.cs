using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace miniCar;

/// <summary>
/// 드리프트할 때 남는 뒷바퀴 타이어 자국과 연기.
/// 드리프트 한 번마다 그 구간만 덮는 작은 클릭 통과 투명 창을 하나 만든다.
/// 드리프트 중에는 자국을 이어 그리고, 끝나면 연기가 흩어진 뒤 자국이 서서히 사라지면 창을 닫는다.
/// (화면 전체를 덮는 창을 매 프레임 다시 그리지 않으므로 가볍다)
/// </summary>
internal sealed class SkidEffects : IDisposable
{
    private const int MaxEffects = 8;
    private const double PuffLife = 0.75;     // 연기 한 덩이 수명 (초)
    private const double PuffInterval = 0.035;

    private sealed class Puff
    {
        public double X, Y, Vx, Vy, Age, Size;
    }

    private sealed class Effect
    {
        public LayeredWindow? Window; // 처음 보일 때 만듦
        public readonly List<(PointF l, PointF r, float strength)> Marks = new();
        public readonly List<Puff> Puffs = new();
        public bool Active = true;   // 아직 드리프트 중
        public double Fade;          // 끝난 뒤 지난 시간
        public double PuffTimer;
        public float Scale = 1;
        public IntPtr Bitmap;
        public Size Size;
        public Point Origin;
        public bool Shown;
    }

    private readonly List<Effect> _effects = new();
    private readonly Random _rng = new();
    private Effect? _current;

    /// <summary>자국이 완전히 사라지기까지 걸리는 시간 (초)</summary>
    public double FadeSeconds { get; set; } = 2.5;
    public bool Enabled { get; set; } = true;
    /// <summary>테스트용: 창을 만들지 않고 계산만</summary>
    internal bool Headless { get; set; }

    /// <summary>아직 그리거나 사라지는 중인 효과가 있는지 (프레임 루프를 계속 돌려야 하는지)</summary>
    public bool Active => _effects.Count > 0;

    /// <param name="drifting">차가 드리프트 중인지</param>
    /// <param name="rearLeft">왼쪽 뒷바퀴 화면 좌표</param>
    /// <param name="rearRight">오른쪽 뒷바퀴 화면 좌표</param>
    /// <param name="strength">0~1, 슬립각 크기 (자국 진하기·연기 양)</param>
    /// <param name="scale">물리 픽셀 배율 (DPI·차 크기 반영)</param>
    /// <param name="keepCarOnTop">새 효과 창이 생겼을 때 차 창을 다시 맨 위로 올리는 콜백</param>
    /// <param name="smoke">연기 양 0~1 (브레이크 자국은 적게)</param>
    public void Update(double dt, bool drifting, PointF rearLeft, PointF rearRight, float strength, float scale, Action keepCarOnTop, float smoke = 1f)
    {
        if (!Enabled)
        {
            Clear();
            return;
        }

        if (drifting)
        {
            if (_current == null)
            {
                if (_effects.Count >= MaxEffects) Remove(_effects[0]);
                _current = new Effect { Scale = scale };
                _effects.Add(_current);
            }
            _current.Marks.Add((rearLeft, rearRight, Math.Max(0.15f, strength)));

            // 뒷바퀴에서 연기
            _current.PuffTimer += dt;
            while (_current.PuffTimer >= PuffInterval)
            {
                _current.PuffTimer -= PuffInterval;
                if (_rng.NextDouble() < (0.35 + 0.65 * strength) * smoke)
                {
                    foreach (var w in new[] { rearLeft, rearRight })
                    {
                        _current.Puffs.Add(new Puff
                        {
                            X = w.X + (_rng.NextDouble() - 0.5) * 4 * scale,
                            Y = w.Y + (_rng.NextDouble() - 0.5) * 4 * scale,
                            Vx = (_rng.NextDouble() - 0.5) * 30 * scale,
                            Vy = (_rng.NextDouble() - 0.5) * 30 * scale - 6 * scale,
                            Size = (5 + _rng.NextDouble() * 4) * scale,
                        });
                    }
                }
            }
        }
        else if (_current != null)
        {
            _current.Active = false;
            _current = null;
        }

        bool newWindow = false;
        foreach (var e in _effects.ToArray())
        {
            foreach (var p in e.Puffs)
            {
                p.Age += dt;
                p.X += p.Vx * dt;
                p.Y += p.Vy * dt;
                p.Vx *= Math.Exp(-2 * dt);
                p.Vy *= Math.Exp(-2 * dt);
            }
            e.Puffs.RemoveAll(p => p.Age >= PuffLife);

            byte opacity = 255;
            if (!e.Active && e.Puffs.Count == 0)
            {
                e.Fade += dt;
                double left = 1 - e.Fade / Math.Max(0.1, FadeSeconds);
                if (left <= 0)
                {
                    Remove(e);
                    continue;
                }
                opacity = (byte)Math.Round(255 * left);
            }

            // 드리프트 중이거나 연기가 움직이는 동안은 다시 그리고, 사라지는 중에는 투명도만 바꿈
            if (!Headless && (e.Active || e.Puffs.Count > 0 || e.Bitmap == IntPtr.Zero)) Render(e);
            if (e.Bitmap == IntPtr.Zero || Headless) continue;
            if (!e.Shown)
            {
                e.Window = new LayeredWindow(clickThrough: true);
                e.Window.Show();
                e.Shown = true;
                newWindow = true;
            }
            e.Window!.Present(e.Bitmap, e.Size, e.Origin, opacity);
        }

        if (newWindow) keepCarOnTop(); // 효과 창이 차 위로 올라오지 않게
    }

    /// <summary>테스트/미리보기용: 현재 효과들을 화면 좌표 그대로 하나의 비트맵에 그린다.</summary>
    internal void DrawAll(Graphics g, PointF offset)
    {
        foreach (var e in _effects) Draw(g, e, offset, 1f);
    }

    private void Render(Effect e)
    {
        if (e.Marks.Count == 0 && e.Puffs.Count == 0) return;

        // 자국 + 연기를 감싸는 사각형
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        void Grow(double x, double y, double r)
        {
            minX = (float)Math.Min(minX, x - r); minY = (float)Math.Min(minY, y - r);
            maxX = (float)Math.Max(maxX, x + r); maxY = (float)Math.Max(maxY, y + r);
        }
        float tire = 3.2f * e.Scale;
        foreach (var (l, r, _) in e.Marks) { Grow(l.X, l.Y, tire); Grow(r.X, r.Y, tire); }
        foreach (var p in e.Puffs) Grow(p.X, p.Y, PuffRadius(p) + 2);

        int left = (int)Math.Floor(minX) - 2, top = (int)Math.Floor(minY) - 2;
        int width = Math.Max(1, (int)Math.Ceiling(maxX) - left + 2);
        int height = Math.Max(1, (int)Math.Ceiling(maxY) - top + 2);
        if (width > 4000 || height > 4000) return; // 비정상적으로 큰 경우 안전장치

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            Draw(g, e, new PointF(-left, -top), 1f);
        }

        if (e.Bitmap != IntPtr.Zero) Native.DeleteObject(e.Bitmap);
        e.Bitmap = bmp.GetHbitmap(Color.FromArgb(0));
        e.Size = new Size(width, height);
        e.Origin = new Point(left, top);
    }

    private static void Draw(Graphics g, Effect e, PointF offset, float alphaScale)
    {
        // 타이어 자국: 두 뒷바퀴가 지나간 길. 진하기가 비슷한 구간끼리 이어진 선으로 그려
        // 선분 끝이 겹쳐 점선처럼 보이지 않게 한다 (슬립이 클수록 진하게)
        float width = 3.2f * e.Scale;
        int start = 0;
        while (start < e.Marks.Count - 1)
        {
            int level = AlphaLevel(e.Marks[start + 1].strength);
            int end = start + 1;
            while (end + 1 < e.Marks.Count && AlphaLevel(e.Marks[end + 1].strength) == level) end++;
            float k = level / 10f * alphaScale;
            if (k > 0)
            {
                var lpts = new PointF[end - start + 1];
                var rpts = new PointF[end - start + 1];
                for (int i = start; i <= end; i++)
                {
                    lpts[i - start] = new PointF(e.Marks[i].l.X + offset.X, e.Marks[i].l.Y + offset.Y);
                    rpts[i - start] = new PointF(e.Marks[i].r.X + offset.X, e.Marks[i].r.Y + offset.Y);
                }
                // 어떤 바탕색에서도 보이도록 두 겹으로:
                //  1) 바깥: 넓고 옅은 밝은 테두리 → 어두운 바탕에서 보임
                //  2) 안쪽: 어두운 고무 자국 → 밝은 바탕에서 보임
                using (var halo = new Pen(Color.FromArgb((int)(110 * k), 225, 225, 230), width + 2.6f * e.Scale) { LineJoin = LineJoin.Round })
                {
                    g.DrawLines(halo, lpts);
                    g.DrawLines(halo, rpts);
                }
                using (var core = new Pen(Color.FromArgb((int)(185 * k), 36, 36, 40), width) { LineJoin = LineJoin.Round })
                {
                    g.DrawLines(core, lpts);
                    g.DrawLines(core, rpts);
                }
            }
            start = end;
        }

        // 연기: 커지면서 옅어지는 덩어리. 어떤 바탕색에서도 보이도록
        //  가운데는 밝은 흰색(어두운 바탕에서 보임), 가장자리는 어두운 회색 띠(밝은 바탕에서 보임)
        foreach (var p in e.Puffs)
        {
            double life = p.Age / PuffLife;
            float r = (float)PuffRadius(p);
            double fade = (1 - life) * (1 - life) * alphaScale;
            if (fade <= 0.01 || r <= 0.5f) continue;
            using var path = new GraphicsPath();
            path.AddEllipse((float)p.X + offset.X - r, (float)p.Y + offset.Y - r, 2 * r, 2 * r);
            using var brush = new PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb((int)(225 * fade), 250, 250, 252),
                SurroundColors = new[] { Color.FromArgb(0, 70, 72, 78) },
            };
            // 위치: 0 = 가장자리, 1 = 가운데
            brush.InterpolationColors = new ColorBlend
            {
                Colors = new[]
                {
                    Color.FromArgb(0, 70, 72, 78),                    // 가장자리: 투명
                    Color.FromArgb((int)(165 * fade), 62, 64, 70),   // 어두운 띠
                    Color.FromArgb((int)(185 * fade), 210, 211, 216), // 밝아지기 시작
                    Color.FromArgb((int)(225 * fade), 250, 250, 252), // 가운데: 밝은 흰색
                },
                Positions = new[] { 0f, 0.22f, 0.5f, 1f },
            };
            g.FillPath(brush, path);
        }
    }

    /// <summary>진하기를 10단계로 (같은 단계끼리 한 선으로 묶음). 최대 알파 150</summary>
    private static int AlphaLevel(float strength) => (int)Math.Round(MathX.Clamp(strength, 0, 1) * 10);

    private static double PuffRadius(Puff p) => p.Size * (0.6 + 1.8 * (p.Age / PuffLife));

    private void Remove(Effect e)
    {
        if (e == _current) _current = null;
        if (e.Bitmap != IntPtr.Zero) Native.DeleteObject(e.Bitmap);
        e.Bitmap = IntPtr.Zero;
        if (e.Window != null)
        {
            e.Window.Close();
            e.Window.Dispose();
        }
        _effects.Remove(e);
    }

    public void Clear()
    {
        foreach (var e in _effects.ToArray()) Remove(e);
        _current = null;
    }

    public void Dispose() => Clear();
}
