using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace miniCar;

/// <summary>차 위에 잠깐 뜨는 상태 표시 ("속도 3/7", "수동 주행" 등)</summary>
internal sealed class OsdWindow : LayeredWindow
{
    private const double FadeSeconds = 0.35;

    private IntPtr _hBitmap;
    private Size _size;
    private string? _renderedText;
    private int _renderedDpi;
    private bool _hiddenPresented = true;
    private Point _lastPos;

    private string? _flashText;
    private double _flashRemaining;

    /// <summary>계속 떠 있는 문구 (수동 주행 중 표시). null이면 없음.</summary>
    public string? StickyText { get; set; }

    /// <summary>아직 그려야 할 애니메이션(표시/페이드)이 남았는가</summary>
    public bool IsAnimating => _flashRemaining > 0 || StickyText != null;

    public void Flash(string text, double seconds = 1.4)
    {
        _flashText = text;
        _flashRemaining = seconds;
    }

    public void Tick(double dt, double carX, double carY, int carSide, Rectangle area, int dpi)
    {
        string? text;
        double opacity = 1;

        if (_flashRemaining > 0)
        {
            _flashRemaining -= dt;
            text = _flashText;
            if (StickyText == null && _flashRemaining < FadeSeconds)
                opacity = Math.Max(0, _flashRemaining / FadeSeconds);
            if (_flashRemaining <= 0 && StickyText != null) text = StickyText;
        }
        else
        {
            text = StickyText;
        }

        if (text == null || opacity <= 0)
        {
            HideOsd();
            return;
        }

        if (text != _renderedText || dpi != _renderedDpi) Rebuild(text, dpi);

        int x = (int)Math.Round(carX - _size.Width / 2.0);
        int y = (int)Math.Round(carY - carSide / 2.0 - _size.Height + 4 * dpi / 96.0);
        if (y < area.Top) y = (int)Math.Round(carY + carSide / 2.0 - 4 * dpi / 96.0);
        x = MathX.Clamp(x, area.Left, Math.Max(area.Left, area.Right - _size.Width));
        y = MathX.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - _size.Height));

        _lastPos = new Point(x, y);
        Present(_hBitmap, _size, _lastPos, (byte)Math.Round(255 * opacity));
        _hiddenPresented = false;
    }

    public void HideOsd()
    {
        if (_hiddenPresented || _hBitmap == IntPtr.Zero) return;
        Present(_hBitmap, _size, _lastPos, 0);
        _hiddenPresented = true;
    }

    private void Rebuild(string text, int dpi)
    {
        FreeBitmap();
        float s = dpi / 96f;
        using var font = new Font("Malgun Gothic", 12f * s, FontStyle.Bold, GraphicsUnit.Pixel);

        SizeF textSize;
        using (var probe = new Bitmap(1, 1))
        using (var pg = Graphics.FromImage(probe))
            textSize = pg.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);

        int padX = (int)(10 * s), padY = (int)(5 * s);
        _size = new Size((int)Math.Ceiling(textSize.Width) + padX * 2, (int)Math.Ceiling(textSize.Height) + padY * 2);

        using var bmp = new Bitmap(_size.Width, _size.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            float r = _size.Height / 2f;
            using var path = new GraphicsPath();
            path.AddArc(0, 0, 2 * r, 2 * r, 90, 180);
            path.AddArc(_size.Width - 2 * r - 1, 0, 2 * r, 2 * r, 270, 180);
            path.CloseFigure();
            using var bg = new SolidBrush(Color.FromArgb(215, 22, 22, 28));
            g.FillPath(bg, path);

            using var fg = new SolidBrush(Color.White);
            g.DrawString(text, font, fg, padX, padY, StringFormat.GenericTypographic);
        }

        _hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        _renderedText = text;
        _renderedDpi = dpi;
    }

    private void FreeBitmap()
    {
        if (_hBitmap != IntPtr.Zero)
        {
            Native.DeleteObject(_hBitmap);
            _hBitmap = IntPtr.Zero;
        }
    }

    protected override void Dispose(bool disposing)
    {
        FreeBitmap();
        base.Dispose(disposing);
    }
}
