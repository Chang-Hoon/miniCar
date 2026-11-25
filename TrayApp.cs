using System.Diagnostics;
using Microsoft.Win32;

namespace miniCar;

/// <summary>
/// 앱 전체를 묶는 컨텍스트: 트레이 아이콘, 단축키, 프레임 루프, 상태 전환.
/// 모든 상태 변경은 UI 스레드에서 일어난다.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private AppSettings _settings;
    private readonly MonitorLayout _layout = new();
    private readonly CarSprite _sprite;
    private readonly CarController _car;
    private readonly CarWindow _carWindow = new();
    private readonly OsdWindow _osd = new();
    private readonly GlobalHotkeys _hotkeys = new();
    private readonly KeyboardCapture _capture = new();
    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _housekeeping = new() { Interval = 1000 };

    // 바인딩 (자율 주행: RegisterHotKey / 수동 주행: 키보드 훅에서 직접 매칭)
    private readonly List<(Hotkey hotkey, Action action, bool repeat)> _bindings = new();
    private Hotkey? _manualExitKey;
    private Hotkey? _parkKey;

    // 주차 / 창 이동
    private readonly WindowMover _mover = new();
    private readonly SkidEffects _skids = new();
    private readonly HudWindow _hud = new();
    private readonly BeamWindow _beam = new();
    private Hotkey? _headlightKey;
    private ToolStripMenuItem _headlightItem = null!;
    private bool _parked;
    private WindowMover.Result _lastMoveResult;

    // 프레임 루프
    private readonly Thread _frameThread;
    private readonly ManualResetEventSlim _runGate = new(true);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Action _tickAction;
    private volatile bool _quitting;
    private int _framePending;
    private double _lastTime;

    private bool _paused;

    // 수동 주행 중 멈춰 있으면 일반 키를 원래 앱으로 돌려줌
    private double _lastActiveTime;
    private bool _idle;
    private bool _hiddenForFullscreen;

    // 마우스 드래그
    private double _grabDx, _grabDy;
    private readonly List<(double t, Point p)> _dragSamples = new();

    // 트레이 메뉴
    private ToolStripMenuItem _pauseItem = null!, _manualItem = null!, _fasterItem = null!, _slowerItem = null!, _quitItem = null!;
    private ToolStripMenuItem _styleCarItem = null!, _styleTopDownItem = null!;
    private ToolStripMenuItem _keyGuideItem = null!, _speedHudItem = null!;
    private IntPtr _trayIconHandle;
    private ToolStripMenuItem _designGtItem = null!, _designClassicItem = null!;
    private readonly List<(ToolStripMenuItem item, string hex)> _colorItems = new();
    private int _lastBoostStage;

    public TrayApp()
    {
        _settings = AppSettings.Load(out var loadError);
        _layout.UseWorkArea = _settings.UseWorkArea;
        _layout.Refresh();

        _sprite = new CarSprite(_settings.GetCarColor(), _settings.CarLength, _settings.GetCarDesign());
        _car = new CarController(_layout);
        ApplyDrivingSettings();
        _car.PlaceAtPrimaryCenter();

        _carWindow.DragStarted += OnDragStarted;
        _carWindow.DragEnded += OnDragEnded;
        _carWindow.Clicked += ToggleKeyGuide;
        _carWindow.RightClicked += ShowTrayMenu;

        _beam.Show();       // 불빛은 차보다 아래에 놓이도록 먼저
        _carWindow.Show();
        _osd.Show();
        _hud.Show();

        _capture.KeyDown = OnCapturedKey;

        BuildTray();
        var failures = RegisterHotkeys();

        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        _housekeeping.Tick += (_, _) => Housekeeping();
        _housekeeping.Start();

        _tickAction = Tick;
        _frameThread = new Thread(FrameLoop) { IsBackground = true, Name = "miniCar.Frame" };
        _frameThread.Start();

        Flash($"miniCar · 자율 주행 · 속도 {_settings.SpeedIndex + 1}/{_settings.SpeedLevels.Length}");

        var problems = new List<string>();
        if (loadError != null) problems.Add(loadError);
        if (failures.Count > 0) problems.Add("등록 실패한 단축키 (다른 프로그램이 사용 중): " + string.Join(", ", failures));
        if (problems.Count > 0)
            _tray.ShowBalloonTip(5000, "miniCar", string.Join("\n", problems), ToolTipIcon.Warning);
    }

    // ───────────────────── 프레임 루프 ─────────────────────

    private void FrameLoop()
    {
        while (!_quitting)
        {
            _runGate.Wait();
            if (_quitting) break;

            // 화면 합성 주기(vsync)에 맞춰 깨어남. 실패하면 약 60fps로 대체.
            if (Native.DwmFlush() != 0) Thread.Sleep(15);

            if (Interlocked.Exchange(ref _framePending, 1) == 0)
            {
                try { _carWindow.BeginInvoke(_tickAction); }
                catch (Exception) { return; } // 종료 중 창이 닫힘
            }
        }
    }

    private void Tick()
    {
        try
        {
            double now = _clock.Elapsed.TotalSeconds;
            double dt = MathX.Clamp(now - _lastTime, 0, 0.05);
            _lastTime = now;

            if (_quitting || _hiddenForFullscreen) return;

            var monitor = _layout.MonitorAt(_car.X, _car.Y);
            int dpi = monitor.Dpi;

            if (_carWindow.Dragging)
            {
                // 끄는 중: 마우스를 따라감 (물리 시뮬레이션 정지)
                var p = Cursor.Position;
                _car.DragTo(p.X + _grabDx, p.Y + _grabDy);
                _dragSamples.Add((now, p));
                _dragSamples.RemoveAll(sm => now - sm.t > 0.1);
                monitor = _layout.MonitorAt(_car.X, _car.Y);
                dpi = monitor.Dpi;
            }
            else if (!_paused)
            {
                (_car.HalfLength, _car.HalfWidth) = _sprite.HalfExtents(dpi);
                var input = _capture.ReadInput();
                if (_parked) MoveParkedWindow(dt, dpi, input);
                else _car.Update(dt, dpi, input);
                UpdateIdle(now, input);

                if (_car.BoostStage > _lastBoostStage)
                    Flash(!_car.Launching ? $"부스트 {_car.BoostStage}단" : _car.LaunchedFromStop ? $"부스터 {_car.BoostStage}단 출발!" : $"부스터 {_car.BoostStage}단 업!");
                _lastBoostStage = _car.BoostStage;
                monitor = _layout.MonitorAt(_car.X, _car.Y);
                dpi = monitor.Dpi;
            }

            var lights =
                _parked ? (now % 0.9 < 0.45 ? CarLights.Hazard : CarLights.Normal) : // 비상등 깜빡임
                _car.Braking ? CarLights.Brake :
                _car.Reversing ? CarLights.Reverse :
                _car.Boosting ? BoostFlame(_car.BoostStage) :
                CarLights.Normal;

            if (_settings.Headlights) _beam.ShowBeam(_car.X, _car.Y, _car.DisplayAngle, _sprite.ScaleFor(dpi));
            else _beam.HideBeam();
            IntPtr hBitmap = _sprite.Get(dpi, _car.DisplayAngle, lights, _settings.Headlights, out int side);
            var topLeft = new Point((int)Math.Round(_car.X) - side / 2, (int)Math.Round(_car.Y) - side / 2);
            _carWindow.Present(hBitmap, new Size(side, side), topLeft);

            UpdateSkids(dt, dpi);
            _osd.Tick(dt, _car.X, _car.Y, side, _layout.AreaOf(monitor), dpi);
            UpdateHud(monitor, dpi);
        }
        finally
        {
            // 멈춤 상태에서 OSD 애니메이션·타이어 자국도 끝났으면 루프를 재워서 CPU를 쓰지 않음
            if (_hiddenForFullscreen || (_paused && !_osd.IsAnimating && !_carWindow.Dragging && !_skids.Active)) _runGate.Reset();
            Volatile.Write(ref _framePending, 0);
        }
    }

    /// <summary>
    /// 수동 주행 중 차가 멈춰 있는지 판단해 키 가로채기 범위를 줄인다.
    /// Space는 멈추면 바로, 단독 글자 키(H, P)는 ManualIdleSeconds가 지나면 원래 앱으로 보낸다.
    /// </summary>
    private void UpdateIdle(double now, ManualInput input)
    {
        if (_car.Mode != DriveMode.Manual)
        {
            _idle = false;
            _capture.PassSpace = false;
            return;
        }
        bool stopped = _parked || _car.CurrentSpeed < 2;
        bool active = input.AnyArrow || (!_parked && (input.Brake || !stopped));
        if (active) _lastActiveTime = now;

        // 방향키를 누르고 있으면 출발/창 이동 중이므로 Space(브레이크)도 받음
        _capture.PassSpace = stopped && !input.AnyArrow;

        bool idle = _settings.ManualIdleSeconds > 0 && now - _lastActiveTime >= _settings.ManualIdleSeconds;
        if (idle != _idle)
        {
            _idle = idle;
            RefreshStickyLabel();
        }
    }

    /// <summary>차 아래 속도 표시 갱신</summary>
    private void UpdateHud(MonitorLayout.Monitor monitor, int dpi)
    {
        if (!_settings.ShowSpeedHud) return;
        if (_paused || _carWindow.Dragging || _parked) _car.ClearSpeed();

        // 계기판 폭이 좁으므로 짧게 (B3 = 부스트 3단)
        string label =
            _paused ? "정지" :
            _car.Mode == DriveMode.Auto ? (_car.Drifting ? "드리프트" : "자율") :
            _parked ? "주차" :
            _idle ? "대기" :
            _car.BrakeSliding ? "슬라이드" :
            _car.Reversing ? "후진" :
            _car.BoostStage > 0 ? $"B{_car.BoostStage}" :
            "수동";
        int stage = _car.Mode == DriveMode.Manual && !_car.Reversing ? _car.BoostStage : 0;
        double[] multipliers = _car.Mode == DriveMode.Manual ? _car.BoostMultipliers : Array.Empty<double>();
        float k = _sprite.ScaleFor(dpi);
        _hud.Tick(label, stage, _car.Speed * 96.0 / dpi, _car.SpeedLogical, multipliers,
            _car.X, _car.Y, 27 * k + 4 * dpi / 96.0, _layout.AreaOf(monitor), dpi);
    }

    /// <summary>부스트 단계 → 불꽃 세기 (1~2단 / 3~4단 / 5~7단 / 8단 이상)</summary>
    private static CarLights BoostFlame(int stage) =>
        stage <= 2 ? CarLights.Boost1 : stage <= 4 ? CarLights.Boost2 : stage <= 7 ? CarLights.Boost3 : CarLights.Boost4;

    /// <summary>드리프트하거나 급브레이크를 밟는 중이면 뒷바퀴 위치에 타이어 자국과 연기를 남김</summary>
    private void UpdateSkids(double dt, int dpi)
    {
        bool braking = _settings.BrakeSkidMarks && _car.BrakeSkidding && !_car.Drifting;
        bool drifting = (_car.Drifting || braking) && !_paused && !_carWindow.Dragging;
        float k = _sprite.ScaleFor(dpi);
        double a = _car.DisplayAngle * Math.PI / 180;
        double cos = Math.Cos(a), sin = Math.Sin(a);
        PointF Wheel(double dx, double dy) =>
            new((float)(_car.X + (cos * dx - sin * dy) * k), (float)(_car.Y + (sin * dx + cos * dy) * k));
        // 뒷바퀴 중심 (설계 좌표: 뒤 차축 x ≈ -13.3, 좌우 y = ±10.3)
        var left = Wheel(-13.3, -10.3);
        var right = Wheel(-13.3, 10.3);
        float strength = braking
            ? (float)MathX.Clamp(_car.CurrentSpeed / Math.Max(1, _settings.SpeedLevels[_settings.SpeedIndex] * dpi / 96.0), 0.35, 0.9) // 빠를수록 진하게
            : (float)MathX.Clamp(Math.Abs(_car.SlipAngle) / Math.Max(1, _settings.DriftSlipDegrees), 0, 1);
        _skids.Update(dt, drifting, left, right, strength, k, () =>
        {
            _carWindow.ReassertTopmost();
            _osd.ReassertTopmost();
            _hud.ReassertTopmost();
        }, smoke: braking ? 0.3f : 1f);
    }

    private void WakeLoop()
    {
        _lastTime = _clock.Elapsed.TotalSeconds;
        _runGate.Set();
    }

    private void Flash(string text)
    {
        _osd.Flash(text);
        _runGate.Set();
    }

    // ───────────────────── 마우스 드래그 ─────────────────────

    /// <param name="down">버튼을 누른 순간의 화면 좌표 (잡은 지점을 기준으로 끌기)</param>
    private void OnDragStarted(Point down)
    {
        _grabDx = _car.X - down.X;
        _grabDy = _car.Y - down.Y;
        _dragSamples.Clear();
        _dragSamples.Add((_clock.Elapsed.TotalSeconds, Cursor.Position));
        WakeLoop();
    }

    /// <summary>
    /// 차를 오른쪽 클릭하면 트레이 아이콘 메뉴를 마우스 위치에 띄운다.
    /// 트레이 아이콘을 오른쪽 클릭했을 때와 똑같이 동작하도록 NotifyIcon 내부의 메뉴 표시 함수를 쓴다
    /// (메뉴를 띄우기 전 포그라운드 처리를 해 줘야 바깥을 클릭했을 때 메뉴가 제대로 닫힘).
    /// </summary>
    private void ShowTrayMenu()
    {
        UpdateMenu();
        try
        {
            var show = typeof(NotifyIcon).GetMethod("ShowContextMenu",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (show != null)
            {
                show.Invoke(_tray, null);
                return;
            }
        }
        catch
        {
            // 아래 대체 방식으로
        }
        _tray.ContextMenuStrip?.Show(Cursor.Position);
    }

    /// <summary>헤드라이트 켜기/끄기 (설정에 저장)</summary>
    private void ToggleHeadlights()
    {
        _settings.Headlights = !_settings.Headlights;
        _settings.Save();
        Flash(_settings.Headlights ? "헤드라이트 켜짐" : "헤드라이트 꺼짐");
        if (!_settings.Headlights) _beam.HideBeam();
        UpdateMenu();
        _runGate.Set();
    }

    /// <summary>차를 클릭하면 키 안내 팝업 켜기/끄기 (설정에 저장)</summary>
    private void ToggleKeyGuide() => SetKeyGuide(!_settings.ShowKeyGuide);

    private void SetKeyGuide(bool on)
    {
        _settings.ShowKeyGuide = on;
        _settings.Save();
        RefreshStickyLabel();
        Flash(on ? "키 안내 켜짐 (차를 클릭하면 끔)" : "키 안내 꺼짐 (차를 클릭하면 켬)");
        UpdateMenu();
    }

    private void SetSpeedHud(bool on)
    {
        _settings.ShowSpeedHud = on;
        _settings.Save();
        if (!on) _hud.HideHud();
        _runGate.Set();
        UpdateMenu();
    }

    private void OnDragEnded()
    {
        // 마지막 0.1초 동안의 마우스 이동으로 던지는 속도 계산
        double now = _clock.Elapsed.TotalSeconds;
        var p = Cursor.Position;
        _dragSamples.Add((now, p));
        _dragSamples.RemoveAll(sm => now - sm.t > 0.1);
        if (_dragSamples.Count >= 2)
        {
            var first = _dragSamples[0];
            var last = _dragSamples[_dragSamples.Count - 1];
            double span = last.t - first.t;
            if (span > 0.015)
            {
                int dpi = _layout.MonitorAt(_car.X, _car.Y).Dpi;
                _car.Throw((last.p.X - first.p.X) / span, (last.p.Y - first.p.Y) / span, dpi);
            }
        }
        _dragSamples.Clear();
        WakeLoop();
    }

    // ───────────────────── 동작 ─────────────────────

    private void TogglePause()
    {
        if (!_paused && _car.Mode == DriveMode.Manual)
        {
            // 멈춘 동안 키보드를 계속 빼앗지 않도록 수동 모드도 해제
            SetMode(DriveMode.Auto, announce: false);
        }
        _paused = !_paused;
        Flash(_paused ? "일시정지" : "다시 출발");
        if (!_paused) WakeLoop();
        UpdateMenu();
    }

    private void ToggleMode() =>
        SetMode(_car.Mode == DriveMode.Auto ? DriveMode.Manual : DriveMode.Auto);

    private void SetMode(DriveMode mode, bool announce = true)
    {
        if (mode == _car.Mode) return;

        if (mode == DriveMode.Manual)
        {
            if (_hiddenForFullscreen) return;
            if (!_capture.Start())
            {
                Flash("키보드 캡처를 시작하지 못했습니다");
                return;
            }
            _car.SetMode(DriveMode.Manual);
            SetParked(false);
            _lastActiveTime = _clock.Elapsed.TotalSeconds;
            _idle = false;
            _capture.PassSpace = true; // 막 들어온 순간에는 멈춰 있음
            RefreshStickyLabel();
            if (!_settings.ShowKeyGuide) Flash("수동 주행 · 차를 클릭하면 키 안내");
            if (_paused) { _paused = false; WakeLoop(); }
            _runGate.Set();
        }
        else
        {
            _capture.Stop();
            SetParked(false);
            _mover.Release();
            _car.SetMode(DriveMode.Auto);
            RefreshStickyLabel();
            if (announce) Flash("자율 주행");
        }
        UpdateMenu();
    }

    private void ApplyDrivingSettings()
    {
        _car.SpeedLogical = _settings.SpeedLevels[_settings.SpeedIndex];
        _car.BounceJitterDegrees = _settings.BounceJitterDegrees;
        _car.AccelSeconds = _settings.ManualAccelSeconds;
        _car.CoastSeconds = _settings.ManualCoastSeconds;
        _car.BrakeSeconds = _settings.ManualBrakeSeconds;
        _car.SteeringDegreesPerSecond = _settings.SteeringDegreesPerSecond;
        _car.Style = _settings.GetControlStyle();
        _car.BoostMultipliers = _settings.ManualBoostMultipliers;
        _car.BoostHoldSeconds = _settings.ManualBoostHoldSeconds;
        _car.DriftEnabled = _settings.AutoDrift;
        _car.DriftTurnDegreesPerSecond = _settings.DriftTurnDegreesPerSecond;
        _car.DriftSlipDegrees = _settings.DriftSlipDegrees;
        _car.BoostDrift = _settings.BoostDrift;
        _car.LaunchTapBoost = _settings.LaunchTapBoost;
        _car.LaunchTapSeconds = _settings.LaunchTapSeconds;
        _car.BrakeSlide = _settings.BrakeSlide;
        _car.BrakeSlideDegrees = _settings.BrakeSlideDegrees;
        _skids.Enabled = _settings.SkidMarks;
        _skids.FadeSeconds = _settings.SkidMarkFadeSeconds;
        _mover.ScrollNotchesPerSecond = _settings.ScrollNotchesPerSecond;
        _mover.UseRealInput = _settings.ScrollMethod != "Message";
    }

    private void SetControlStyle(ControlStyle style)
    {
        _settings.ManualControlStyle = style == ControlStyle.TopDown ? "TopDown" : "Car";
        _settings.Save();
        _car.Style = style;
        RefreshStickyLabel();
        Flash(style == ControlStyle.Car ? "조작 방식: 자동차식" : "조작 방식: 탑뷰 게임식 (16방향)");
        UpdateMenu();
    }

    // ───────────────────── 주차 / 창 이동 ─────────────────────

    /// <summary>주차 중에는 차를 클릭 통과로 만들어 차 아래 앱이 휠 입력을 받게 한다.</summary>
    private void SetParked(bool parked)
    {
        _parked = parked;
        _carWindow.SetClickThrough(parked);
    }

    private Point CarPoint() => new((int)Math.Round(_car.X), (int)Math.Round(_car.Y));

    private void TogglePark()
    {
        if (_car.Mode != DriveMode.Manual) return;
        _lastActiveTime = _clock.Elapsed.TotalSeconds; // 주차/해제 직후에는 다시 키를 받음
        _idle = false;

        if (_parked)
        {
            SetParked(false);
            _mover.Release();
            RefreshStickyLabel();
            Flash("주차 해제");
            return;
        }

        if (_car.CurrentSpeed > 3)
        {
            Flash("완전히 멈춘 뒤에 주차할 수 있습니다 (Space: 브레이크)");
            return;
        }

        _car.Stop();
        SetParked(true);
        _lastMoveResult = WindowMover.Result.Idle;
        bool found = _mover.Acquire(CarPoint());
        RefreshStickyLabel();
        Flash(found ? $"주차 · 옮길 창: {_mover.TargetTitle}" : "주차 · 차 아래에 창이 없습니다");
        _runGate.Set();
    }

    private void MoveParkedWindow(double dt, int dpi, ManualInput k)
    {
        int dx = (k.Right ? 1 : 0) - (k.Left ? 1 : 0);
        int dy = (k.Down ? 1 : 0) - (k.Up ? 1 : 0);

        // 대상 창이 없거나 닫혔으면, 방향키를 누를 때 차 아래 창을 다시 찾음
        if ((dx != 0 || dy != 0) && !_mover.HasTarget && _mover.Acquire(CarPoint()))
            Flash($"옮길 창: {_mover.TargetTitle}");

        var result = _mover.Move(dt, dx, dy, _settings.WindowMoveSpeed * dpi / 96.0, _layout, CarPoint(), out var moved);
        if (result == WindowMover.Result.Moved && _settings.ParkCarFollowsWindow && moved != Point.Empty)
            _car.DragTo(_car.X + moved.X, _car.Y + moved.Y);

        if (result != _lastMoveResult)
        {
            switch (result)
            {
                case WindowMover.Result.NoWindow: Flash("차 아래에 옮길 창이 없습니다"); break;
                case WindowMover.Result.Scrolled: Flash("최대화된 창 · 방향키로 스크롤"); break;
                case WindowMover.Result.AccessDenied: Flash("이 창은 옮길 수 없습니다 (관리자 권한 창 등)"); break;
                case WindowMover.Result.NotResponding: Flash("응답하지 않는 창입니다"); break;
            }
            _lastMoveResult = result;
        }
    }

    private void ChangeSpeed(int delta)
    {
        int idx = MathX.Clamp(_settings.SpeedIndex + delta, 0, _settings.SpeedLevels.Length - 1);
        _settings.SpeedIndex = idx;
        _car.SpeedLogical = _settings.SpeedLevels[idx];
        Flash($"속도 {idx + 1}/{_settings.SpeedLevels.Length}");
        UpdateMenu();
    }

    /// <summary>
    /// 수동 모드에서 훅이 받은 키 (운전 키 제외). miniCar가 쓰는 키면 true → 다른 앱으로 가지 않음.
    /// 훅 콜백 안이므로 실제 처리는 메시지 큐로 미룬다.
    /// </summary>
    private bool OnCapturedKey(Keys key, Mod mods, bool repeat)
    {
        if (_manualExitKey is { } exit && key == exit.Key && mods == exit.Modifiers)
        {
            if (!repeat) _carWindow.BeginInvoke(() => SetMode(DriveMode.Auto));
            return true;
        }
        if (_headlightKey is { } head && key == head.Key && mods == head.Modifiers)
        {
            if (_idle && mods == Mod.None) return false; // 멈춘 지 오래됨 → 'H' 타이핑으로 돌려줌
            if (!repeat) _carWindow.BeginInvoke(ToggleHeadlights);
            return true;
        }
        if (_parkKey is { } park && key == park.Key && mods == park.Modifiers)
        {
            if (_idle && !_parked && mods == Mod.None) return false; // 주차 중에는 주차 해제용으로 계속 받음
            if (!repeat) _carWindow.BeginInvoke(TogglePark);
            return true;
        }
        foreach (var (hotkey, action, allowRepeat) in _bindings)
        {
            if (hotkey.Key == key && hotkey.Modifiers == mods)
            {
                if (allowRepeat || !repeat) _carWindow.BeginInvoke(action);
                return true;
            }
        }
        return false; // miniCar가 쓰지 않는 키 → 원래대로 앱/Windows로
    }

    private void RefreshStickyLabel()
    {
        if (_car.Mode != DriveMode.Manual || !_settings.ShowKeyGuide) { _osd.StickyText = null; return; }
        string exit = _manualExitKey?.ToString() ?? "모드 전환 키";
        string park = _parkKey?.ToString() ?? "-";
        _osd.StickyText = _idle && !_parked
            ? $"대기 중 · 방향키로 출발 · Space·{_headlightKey?.ToString() ?? "H"}·{park} 등 일반 키는 다른 앱으로 · {exit}: 자율 주행"
            : _parked
            ? $"주차 중 · 방향키: 창 이동 (최대화된 창은 스크롤) · {park}: 주차 해제 · {exit}: 자율 주행"
            : _car.Style == ControlStyle.Car
                ? $"자동차식 · ↑ 가속  ↓ 브레이크/후진  ←→ 핸들 · Space 브레이크 · {park} 주차 · {exit} 자율 주행"
                : $"탑뷰 16방향 · 방향키 이동 (옆 키 톡: 22.5°) · Space 브레이크 · {park} 주차 · {exit} 자율 주행";
    }

    // ───────────────────── 단축키 ─────────────────────

    private List<string> RegisterHotkeys()
    {
        _hotkeys.UnregisterAll();
        _bindings.Clear();
        var failures = new List<string>();

        void Bind(string name, string text, Action action, bool repeat)
        {
            if (!Hotkey.TryParse(text, out var hk))
            {
                failures.Add($"{name}='{text}'(형식 오류)");
                return;
            }
            _bindings.Add((hk, action, repeat));
            if (!_hotkeys.Register(hk, action, repeat)) failures.Add($"{name}={hk}");
        }

        Bind("일시정지", _settings.TogglePause, TogglePause, false);
        Bind("모드 전환", _settings.ToggleMode, ToggleMode, false);
        Bind("빠르게", _settings.SpeedUp, () => ChangeSpeed(+1), true);
        Bind("느리게", _settings.SpeedDown, () => ChangeSpeed(-1), true);
        Bind("종료", _settings.Quit, Quit, false);
        Bind("헤드라이트", _settings.HeadlightToggle, ToggleHeadlights, false);

        _manualExitKey = Hotkey.TryParse(_settings.ManualExitKey, out var exitKey) ? exitKey : null;
        _parkKey = Hotkey.TryParse(_settings.ParkKey, out var parkKey) ? parkKey : null;
        _headlightKey = Hotkey.TryParse(_settings.HeadlightKey, out var headKey) ? headKey : null;

        UpdateMenuShortcuts();
        return failures;
    }

    // ───────────────────── 트레이 ─────────────────────

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        _pauseItem = new ToolStripMenuItem("일시정지", null, (_, _) => TogglePause());
        _manualItem = new ToolStripMenuItem("수동 주행", null, (_, _) => ToggleMode());
        _fasterItem = new ToolStripMenuItem("빠르게", null, (_, _) => ChangeSpeed(+1));
        _slowerItem = new ToolStripMenuItem("느리게", null, (_, _) => ChangeSpeed(-1));
        _quitItem = new ToolStripMenuItem("종료", null, (_, _) => Quit());
        _styleCarItem = new ToolStripMenuItem("자동차식 (↑ 가속, ←→ 핸들)", null, (_, _) => SetControlStyle(ControlStyle.Car));
        _styleTopDownItem = new ToolStripMenuItem("탑뷰 게임식 (16방향)", null, (_, _) => SetControlStyle(ControlStyle.TopDown));
        var styleMenu = new ToolStripMenuItem("수동 조작 방식");
        styleMenu.DropDownItems.AddRange(new ToolStripItem[] { _styleCarItem, _styleTopDownItem });

        _designGtItem = new ToolStripMenuItem("GT (기본)", null, (_, _) => SetCarLook(design: "GT"));
        _designClassicItem = new ToolStripMenuItem("클래식 (레이싱 스트라이프)", null, (_, _) => SetCarLook(design: "Classic"));
        var colorMenu = new ToolStripMenuItem("색상");
        foreach (var (name, hex) in CarSprite.ColorPresets)
        {
            var item = new ToolStripMenuItem(name, SwatchImage(hex), (_, _) => SetCarLook(colorHex: hex));
            _colorItems.Add((item, hex));
            colorMenu.DropDownItems.Add(item);
        }
        _keyGuideItem = new ToolStripMenuItem("키 안내 보기 (차 클릭으로도 전환)", null, (_, _) => SetKeyGuide(!_settings.ShowKeyGuide));
        _speedHudItem = new ToolStripMenuItem("속도 표시", null, (_, _) => SetSpeedHud(!_settings.ShowSpeedHud));
        _headlightItem = new ToolStripMenuItem("헤드라이트", null, (_, _) => ToggleHeadlights());
        var lookMenu = new ToolStripMenuItem("차 디자인");
        lookMenu.DropDownItems.AddRange(new ToolStripItem[] { _designGtItem, _designClassicItem, new ToolStripSeparator(), colorMenu });

        menu.Items.AddRange(new ToolStripItem[]
        {
            _pauseItem, _manualItem, styleMenu, lookMenu, _headlightItem, _keyGuideItem, _speedHudItem, new ToolStripSeparator(),
            _fasterItem, _slowerItem, new ToolStripSeparator(),
            new ToolStripMenuItem("설정 파일 열기", null, (_, _) => OpenSettingsFile()),
            new ToolStripMenuItem("설정 다시 불러오기", null, (_, _) => ReloadSettings()),
            new ToolStripMenuItem("모니터 정보 복사 (문제 진단용)", null, (_, _) => CopyMonitorInfo()),
            new ToolStripSeparator(),
            _quitItem,
        });

        UpdateTrayIcon();
        _tray.Text = "miniCar";
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => TogglePause();
        _tray.Visible = true;
        UpdateMenu();
    }

    private void SetCarLook(string? design = null, string? colorHex = null)
    {
        if (design != null) _settings.CarDesign = design;
        if (colorHex != null) _settings.CarColor = colorHex;
        _settings.Save();
        _sprite.Reconfigure(_settings.GetCarColor(), _settings.CarLength, _settings.GetCarDesign());
        UpdateTrayIcon();
        UpdateMenu();
        _runGate.Set(); // 멈춰 있어도 바뀐 모습을 바로 그림
    }

    private void UpdateTrayIcon()
    {
        using var iconBmp = _sprite.RenderIcon(32);
        IntPtr newHandle = iconBmp.GetHicon();
        _tray.Icon = Icon.FromHandle(newHandle);
        if (_trayIconHandle != IntPtr.Zero) Native.DestroyIcon(_trayIconHandle);
        _trayIconHandle = newHandle;
    }

    private static Bitmap SwatchImage(string hex)
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var b = new SolidBrush(ColorTranslator.FromHtml(hex));
        using var p = new Pen(Color.FromArgb(120, 0, 0, 0));
        g.FillEllipse(b, 1, 1, 13, 13);
        g.DrawEllipse(p, 1, 1, 13, 13);
        return bmp;
    }

    private void UpdateMenu()
    {
        if (_pauseItem == null) return;
        _pauseItem.Checked = _paused;
        _manualItem.Checked = _car.Mode == DriveMode.Manual;
        _styleCarItem.Checked = _car.Style == ControlStyle.Car;
        _styleTopDownItem.Checked = _car.Style == ControlStyle.TopDown;
        _designGtItem.Checked = _settings.GetCarDesign() == CarDesign.GT;
        _designClassicItem.Checked = _settings.GetCarDesign() == CarDesign.Classic;
        _keyGuideItem.Checked = _settings.ShowKeyGuide;
        _speedHudItem.Checked = _settings.ShowSpeedHud;
        _headlightItem.Checked = _settings.Headlights;
        foreach (var (item, hex) in _colorItems)
            item.Checked = string.Equals(hex, _settings.CarColor?.Trim(), StringComparison.OrdinalIgnoreCase);
        _fasterItem.Enabled = _settings.SpeedIndex < _settings.SpeedLevels.Length - 1;
        _slowerItem.Enabled = _settings.SpeedIndex > 0;
        string mode = _car.Mode == DriveMode.Manual ? "수동" : "자율";
        string state = _paused ? " · 일시정지" : "";
        _tray.Text = $"miniCar · {mode} · 속도 {_settings.SpeedIndex + 1}/{_settings.SpeedLevels.Length}{state}";
    }

    private void UpdateMenuShortcuts()
    {
        if (_pauseItem == null) return;
        static string Show(string s) => Hotkey.TryParse(s, out var h) ? h.ToString() : "";
        _pauseItem.ShortcutKeyDisplayString = Show(_settings.TogglePause);
        _manualItem.ShortcutKeyDisplayString = Show(_settings.ToggleMode);
        _fasterItem.ShortcutKeyDisplayString = Show(_settings.SpeedUp);
        _slowerItem.ShortcutKeyDisplayString = Show(_settings.SpeedDown);
        _quitItem.ShortcutKeyDisplayString = Show(_settings.Quit);
        if (_headlightItem != null) _headlightItem.ShortcutKeyDisplayString = Show(_settings.HeadlightToggle);
    }

    private void OpenSettingsFile()
    {
        _settings.Save();
        try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AppSettings.FilePath}\"") { UseShellExecute = true }); }
        catch (Exception ex) { _tray.ShowBalloonTip(3000, "miniCar", ex.Message, ToolTipIcon.Error); }
    }

    private void CopyMonitorInfo()
    {
        _layout.Refresh();
        var text = _layout.Describe();
        try
        {
            Clipboard.SetText(text);
            _tray.ShowBalloonTip(3000, "miniCar", "모니터 정보를 클립보드에 복사했습니다.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(3000, "miniCar", "클립보드 복사 실패: " + ex.Message, ToolTipIcon.Error);
        }
    }

    private void ReloadSettings()
    {
        var reloaded = AppSettings.Load(out var error);
        _settings = reloaded;
        _layout.UseWorkArea = _settings.UseWorkArea;
        _layout.Refresh();
        _sprite.Reconfigure(_settings.GetCarColor(), _settings.CarLength, _settings.GetCarDesign());
        UpdateTrayIcon();
        ApplyDrivingSettings();
        if (!_settings.ShowSpeedHud) _hud.HideHud();
        _runGate.Set();

        var failures = RegisterHotkeys();
        RefreshStickyLabel();
        UpdateMenu();

        var problems = new List<string>();
        if (error != null) problems.Add(error);
        if (failures.Count > 0) problems.Add("등록 실패한 단축키: " + string.Join(", ", failures));
        if (problems.Count > 0) _tray.ShowBalloonTip(5000, "miniCar", string.Join("\n", problems), ToolTipIcon.Warning);
        else Flash("설정을 다시 불러왔습니다");
    }

    // ───────────────────── 주기 점검 (1초) ─────────────────────

    private void Housekeeping()
    {
        _layout.Refresh();
        _beam.ReassertTopmost();   // 불빛 → 차 → 안내 순서로 위에 올라가도록
        _carWindow.ReassertTopmost();
        _osd.ReassertTopmost();
        _hud.ReassertTopmost();

        bool fullscreen = false;
        if (_settings.HideOnFullscreen && Native.SHQueryUserNotificationState(out int state) == 0)
        {
            // 1: 화면보호기/잠금, 2: 전체 화면 앱, 3: D3D 전체 화면, 4: 프레젠테이션 모드
            fullscreen = state is 1 or 2 or 3 or 4;
        }

        if (fullscreen != _hiddenForFullscreen)
        {
            _hiddenForFullscreen = fullscreen;
            if (fullscreen)
            {
                SetMode(DriveMode.Auto, announce: false); // 숨겨진 채로 키를 뺏지 않도록
                _skids.Clear();
                _carWindow.Hide();
                _osd.Hide();
                _hud.Hide();
                _beam.Hide();
            }
            else
            {
                _carWindow.Show();
                _beam.Show();
                _carWindow.ReassertTopmost();
                _osd.Show();
                _hud.Show();
                WakeLoop();
            }
        }
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        if (_carWindow.IsHandleCreated)
            _carWindow.BeginInvoke(() => { _layout.Refresh(); WakeLoop(); });
    }

    // ───────────────────── 종료 ─────────────────────

    private void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _runGate.Set();

        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _housekeeping.Stop();
        _capture.Dispose();
        _hotkeys.Dispose();
        _settings.Save();

        _tray.Visible = false;
        _tray.Dispose();
        if (_trayIconHandle != IntPtr.Zero) Native.DestroyIcon(_trayIconHandle);

        _carWindow.Close();
        _osd.Close();
        _hud.Close();
        _beam.Close();
        _skids.Dispose();
        _sprite.Dispose();
        ExitThread();
    }
}
