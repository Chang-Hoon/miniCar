namespace miniCar;

/// <summary>
/// 차를 그리는 창. 차 몸체(불투명 픽셀)만 마우스를 받고, 투명한 주변은 클릭이 통과한다.
/// 클릭해도 포커스를 가져가지 않으므로 작업 중인 앱의 입력은 그대로 유지된다.
/// </summary>
internal sealed class CarWindow : LayeredWindow
{
    /// <summary>
    /// 왼쪽 버튼을 누른 채 DragThreshold 이상 움직이면 끌기 시작.
    /// 인자 = 버튼을 누른 순간의 화면 좌표 (잡은 지점 기준으로 끌기 위해)
    /// </summary>
    public event Action<Point>? DragStarted;
    /// <summary>끌기 종료</summary>
    public event Action? DragEnded;
    /// <summary>움직이지 않고 짧게 클릭 (차를 클릭해서 키 안내 켜고 끄기 등)</summary>
    public event Action? Clicked;
    /// <summary>오른쪽 버튼 클릭 (메뉴 띄우기)</summary>
    public event Action? RightClicked;

    /// <summary>클릭과 끌기를 가르는 이동 거리 (px)</summary>
    public const int DragThreshold = 4;
    /// <summary>이 시간보다 오래 누르고 있다 떼면 클릭으로 보지 않음 (초)</summary>
    public const double ClickMaxSeconds = 0.5;

    public bool Dragging { get; private set; }

    private bool _pressed;
    private Point _downPos;
    private DateTime _downTime;

    public CarWindow() : base(clickThrough: false)
    {
        Cursor = Cursors.Hand;
    }

    /// <summary>
    /// 클릭 통과 여부를 실행 중에 바꾼다. 주차 중에는 차 아래 앱이 휠 입력을 받을 수 있도록 통과시킨다
    /// (Chrome 등은 휠 좌표 아래 창이 다른 프로그램이면 메시지를 버림).
    /// </summary>
    public void SetClickThrough(bool on)
    {
        if (!IsHandleCreated) return;
        if (on) EndDrag();
        int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
        int updated = on ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT;
        if (updated != ex) Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, updated);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || _pressed) return;
        _pressed = true;
        _downPos = Cursor.Position;
        _downTime = DateTime.UtcNow;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed || Dragging) return;
        if (IsDragMove(_downPos, Cursor.Position))
        {
            Dragging = true;
            Cursor = Cursors.SizeAll;
            DragStarted?.Invoke(_downPos);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right && !_pressed)
        {
            RightClicked?.Invoke();
            return;
        }
        if (e.Button != MouseButtons.Left || !_pressed) return;
        bool wasDragging = Dragging;
        bool click = !wasDragging && IsClick(_downPos, Cursor.Position, (DateTime.UtcNow - _downTime).TotalSeconds);
        Release();
        if (click) Clicked?.Invoke();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) Release(); // Alt+Tab 등으로 캡처를 잃은 경우
    }

    /// <summary>누른 지점에서 DragThreshold 이상 움직였으면 끌기</summary>
    internal static bool IsDragMove(Point down, Point now) =>
        Math.Abs(now.X - down.X) >= DragThreshold || Math.Abs(now.Y - down.Y) >= DragThreshold;

    /// <summary>거의 움직이지 않고 짧게 눌렀다 뗐으면 클릭</summary>
    internal static bool IsClick(Point down, Point up, double seconds) =>
        !IsDragMove(down, up) && seconds <= ClickMaxSeconds;

    private void EndDrag() => Release();

    private void Release()
    {
        bool wasDragging = Dragging;
        _pressed = false;
        Dragging = false;
        Capture = false;
        Cursor = Cursors.Hand;
        if (wasDragging) DragEnded?.Invoke();
    }
}
