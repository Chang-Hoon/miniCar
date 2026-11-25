using static miniCar.Native;

namespace miniCar;

/// <summary>
/// 픽셀 단위 알파를 가진 최상위, 클릭 통과, 포커스 비활성 창.
/// UpdateLayeredWindow로 비트맵과 위치를 한 번에 갱신한다.
/// </summary>
internal class LayeredWindow : Form
{
    private readonly bool _clickThrough;

    /// <param name="clickThrough">true: 모든 클릭 통과 / false: 불투명 픽셀만 클릭을 받음 (알파 0인 곳은 통과)</param>
    public LayeredWindow(bool clickThrough = true)
    {
        _clickThrough = clickThrough;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        Size = new Size(1, 1);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
            if (_clickThrough) cp.ExStyle |= WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_MOUSEACTIVATE:
                m.Result = (IntPtr)MA_NOACTIVATE;
                return;
            case WM_DPICHANGED:
                // WinForms의 자동 크기 조정/이동을 막는다. 크기와 위치는 매 프레임 직접 지정함.
                m.Result = IntPtr.Zero;
                return;
        }
        base.WndProc(ref m);
    }

    /// <summary>미리 곱해진 알파(premultiplied)의 32bpp HBITMAP을 화면에 표시</summary>
    public void Present(IntPtr hBitmap, Size size, Point topLeft, byte opacity = 255)
    {
        if (!IsHandleCreated || IsDisposed || hBitmap == IntPtr.Zero) return;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr old = SelectObject(memDc, hBitmap);
        try
        {
            var dst = new POINT(topLeft.X, topLeft.Y);
            var sz = new SIZE(size.Width, size.Height);
            var src = new POINT(0, 0);
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                SourceConstantAlpha = opacity,
                AlphaFormat = AC_SRC_ALPHA,
            };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref sz, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>다른 TOPMOST 창에 밀렸을 때 다시 맨 위로</summary>
    public void ReassertTopmost()
    {
        if (IsHandleCreated && Visible)
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
