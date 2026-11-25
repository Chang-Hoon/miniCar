namespace miniCar;

internal enum DriveMode { Auto, Manual }

/// <summary>수동 주행 조작 방식</summary>
internal enum ControlStyle
{
    /// <summary>자동차식: ↑ 가속, ↓ 브레이크/후진, ←→ 핸들</summary>
    Car,
    /// <summary>탑뷰 게임식: 방향키 = 화면 방향, 16방향</summary>
    TopDown,
}

/// <summary>한 프레임의 수동 주행 입력</summary>
internal struct ManualInput
{
    public bool Up, Down, Left, Right, Brake;
    public bool AnyArrow => Up || Down || Left || Right;
}

/// <summary>
/// 차의 위치/방향 시뮬레이션. 좌표는 가상 화면 물리 픽셀, 각도는 화면 기준
/// (0° = 오른쪽, 90° = 아래쪽, 시계 방향 증가).
/// </summary>
internal sealed class CarController
{
    private const double AutoTurnRate = 540;    // 표시 각도 회전 속도 (°/s)
    private const double TopDownTurnRate = 600;
    private const double Grip = 5;              // 탑뷰: 옆으로 미끄러지는 속도가 줄어드는 정도
    private const double ReverseRatio = 0.35;   // 자동차식: 후진 최고 속도 비율

    private readonly MonitorLayout _layout;
    private readonly Random _rng = new();
    private readonly Direction16 _dir16 = new();
    private double _vx, _vy;   // 수동 모드 / 던지기 속도 (물리 px/s)
    private int _lastDpi = 96;

    public double X { get; private set; }
    public double Y { get; private set; }
    /// <summary>차가 바라보는 방향 (자율 주행에서는 진행 방향)</summary>
    public double Heading { get; private set; }
    /// <summary>화면에 그려지는 차 앞부분 방향</summary>
    public double DisplayAngle { get; private set; }
    public DriveMode Mode { get; private set; } = DriveMode.Auto;
    public ControlStyle Style { get; set; } = ControlStyle.Car;
    /// <summary>브레이크등 표시용</summary>
    public bool Braking { get; private set; }
    /// <summary>자동차식에서 후진 중</summary>
    public bool Reversing { get; private set; }

    /// <summary>96 DPI 기준 속도 (px/s). 자율 주행 속도이자 수동 주행 최고 속도.</summary>
    public double SpeedLogical { get; set; } = 150;
    public double BounceJitterDegrees { get; set; } = 8;

    /// <summary>수동: 정지 → 최고 속도까지 걸리는 시간(초)</summary>
    public double AccelSeconds { get; set; } = 1.5;
    /// <summary>수동: 키를 놓았을 때 최고 속도 → 정지까지 걸리는 시간(초)</summary>
    public double CoastSeconds { get; set; } = 2.5;
    /// <summary>수동: 브레이크로 최고 속도 → 정지까지 걸리는 시간(초)</summary>
    public double BrakeSeconds { get; set; } = 0.35;
    /// <summary>자동차식: 핸들을 끝까지 돌렸을 때 회전 속도 (°/s)</summary>
    public double SteeringDegreesPerSecond { get; set; } = 200;

    /// <summary>
    /// 수동: 부스트 단계별 최고 속도 배율 (기본 1.4 / 1.8 / 2.3). 빈 배열이면 부스트 없음.
    /// 가속 키를 누른 채 현재 단계 최고 속도에 BoostHoldSeconds 동안 머무르면 다음 단계로 올라간다.
    /// </summary>
    public double[] BoostMultipliers { get; set; } = DefaultBoostMultipliers;

    /// <summary>기본 부스트 배율 10단계</summary>
    public static readonly double[] DefaultBoostMultipliers = { 1.4, 1.8, 2.3, 2.8, 3.4, 4.0, 4.7, 5.4, 6.2, 7.0 };
    public double BoostHoldSeconds { get; set; } = 1.0;
    /// <summary>부스트 단계에서의 가속력 비율 (높은 단계일수록 더 천천히 붙음)</summary>
    private const double BoostAccelRatio = 0.6;

    /// <summary>
    /// 전진 키를 연타하면 부스터 단계를 (연타 수 - 1)만큼 올림.
    /// 멈춘 상태면 그 단계로 출발, 달리는 중이면 지금 단계에 더함 (2단 중 3연타 → 4단).
    /// </summary>
    public bool LaunchTapBoost { get; set; } = true;
    /// <summary>연타로 인정하는 누름 간격 (초)</summary>
    public double LaunchTapSeconds { get; set; } = 0.35;
    private bool _prevAccelPressed;
    private int _tapCount;
    private int _tapBaseStage;      // 연타를 시작할 때의 부스터 단계
    private bool _tapFromStop;      // 연타를 멈춘 상태에서 시작했는지
    private double _sinceLastTap = 99;
    private bool _launching;

    /// <summary>연타 출발로 부스터를 켠 직후인지 (가속하는 동안 단계 유지)</summary>
    public bool Launching => _launching;
    /// <summary>마지막 연타 부스트가 멈춘 상태에서의 출발이었는지 (false면 달리는 중 단계 상향)</summary>
    public bool LaunchedFromStop => _tapFromStop;

    /// <summary>수동 주행 중 브레이크로 미끄러지며 멈추는 중 (타이어 자국용)</summary>
    public bool BrakeSkidding { get; private set; }

    /// <summary>현재 부스트 단계 (0 = 기본 최고 속도, 1~ = 부스트)</summary>
    public int BoostStage { get; private set; }
    /// <summary>지금 부스트로 가속 중인지 (불꽃 표시용)</summary>
    public bool Boosting { get; private set; }
    private double _boostTimer;

    // ── 자율 주행 드리프트 ──
    /// <summary>벽에 닿기 전에 드리프트하며 방향을 바꿈 (false면 벽에서 즉시 반사)</summary>
    public bool DriftEnabled { get; set; } = true;
    /// <summary>드리프트 중 진행 방향이 도는 속도 (°/s). 클수록 벽 가까이에서 급하게 돎</summary>
    public double DriftTurnDegreesPerSecond { get; set; } = 240;
    /// <summary>드리프트 중 차체가 진행 방향보다 더 돌아가는 최대 각도 (뒤가 바깥으로 미끄러지는 정도)</summary>
    public double DriftSlipDegrees { get; set; } = 35;

    private bool _drifting;
    private int _driftDir;
    private double _driftTotal, _driftTurned, _driftOmega, _driftTarget;

    /// <summary>테스트용 통계: 시작한 드리프트 수 / 벽에 직접 닿아 즉시 반사한 수 / 트랙 선에 부딪힌 수</summary>
    internal int DriftCount, WallHitCount, TrackHitCount;

    /// <summary>사용자가 그린 트랙. 선은 화면 가장자리처럼 벽으로 작용한다 (null 또는 빈 트랙 = 없음)</summary>
    public Track? Track { get; set; }

    // 차와 겹친 채로 생긴 선(차 위에 그었거나 차를 선 위에 놓은 경우)은 차가 완전히 벗어날 때까지 통과
    private readonly HashSet<TrackStroke> _knownStrokes = new();
    private readonly HashSet<TrackStroke> _ghostStrokes = new();
    private bool _recheckOverlap = true;

    /// <summary>수동 주행: 부스트 중 방향을 바꾸면 드리프트</summary>
    public bool BoostDrift { get; set; } = true;
    private bool _manualDrifting;
    private bool _wallContact; // 자동차식: 지난 프레임에 벽에 막혔는지
    private const double SlipBuildRate = 220;   // 슬립각이 커지는 속도 (°/s)
    private const double SlipRecoverRate = 140; // 핸들을 풀었을 때 차체가 다시 정렬되는 속도 (°/s)
    private const double DriftGrip = 1.4;       // 탑뷰: 드리프트 중 접지력 (평소 5)

    /// <summary>수동 주행: 달리면서 브레이크 + 방향키를 같이 누르면 차체가 옆으로 돌며 미끄러짐</summary>
    public bool BrakeSlide { get; set; } = true;
    /// <summary>브레이크 슬라이드 때 차체가 진행 방향에서 최대로 돌아가는 각도 (°)</summary>
    public double BrakeSlideDegrees { get; set; } = 75;
    private const double SlideYawRate = 260;     // 슬라이드 중 차체가 도는 속도 (°/s)
    private const double SlideCarryRate = 35;    // 슬라이드 중 진행 방향이 차체 쪽으로 따라 도는 속도 (°/s)
    private const double SlideBrakeRatio = 0.55; // 슬라이드 중 감속은 곧게 밟을 때의 이 비율 (더 길게 미끄러짐)
    private const double SlideStartRatio = 0.25; // 기본 최고 속도의 이 비율 이상일 때만 시작
    private int _slidePhase; // 0 = 없음, 1 = 미끄러지는 중, 2 = 브레이크를 떼서 접지 회복 중

    /// <summary>브레이크 슬라이드 중 (브레이크 + 방향키로 옆으로 미끄러지는 중)</summary>
    public bool BrakeSliding => _slidePhase == 1;

    /// <summary>지금 드리프트 중인지 (자율 주행 드리프트 또는 수동 주행 부스트 드리프트, 타이어 자국·연기 표시용)</summary>
    public bool Drifting => _drifting || _manualDrifting;
    /// <summary>차체 방향 - 진행 방향 (드리프트 슬립각, °)</summary>
    public double SlipAngle { get; private set; }

    /// <summary>물리 픽셀 기준 차 크기 (매 프레임 DPI에 맞춰 갱신)</summary>
    public double HalfLength { get; set; } = 24;
    public double HalfWidth { get; set; } = 12;

    /// <summary>화면에 보여 줄 실제 속력 (물리 px/s). 자율 주행(드리프트 감속 포함)·수동 주행 모두</summary>
    public double Speed { get; private set; }

    /// <summary>수동 모드 현재 속력 (물리 px/s)</summary>
    public double CurrentSpeed => Math.Sqrt(_vx * _vx + _vy * _vy);

    public CarController(MonitorLayout layout) => _layout = layout;

    public void PlaceAtPrimaryCenter()
    {
        var r = _layout.AreaOf(_layout.Primary);
        X = r.Left + r.Width / 2.0;
        Y = r.Top + r.Height / 2.0;
        Heading = Normalize(new[] { 30, 150, 210, 330 }[_rng.Next(4)] + _rng.NextDouble() * 20 - 10);
        DisplayAngle = Heading;
        _vx = _vy = 0;
        CancelDrift();
        _recheckOverlap = true;
    }

    private void CancelDrift()
    {
        if (_drifting) DisplayAngle = Normalize(Heading + SlipAngle);
        _drifting = false;
        _manualDrifting = false;
        _slidePhase = 0;
        SlipAngle = 0;
    }

    /// <summary>각도 차이 a - b 를 -180~180으로</summary>
    private static double AngleDiff(double a, double b) => ((a - b) % 360 + 540) % 360 - 180;

    /// <summary>속도 벡터를 deg만큼 회전 (화면 기준 시계 방향 +)</summary>
    private void RotateVelocity(double deg)
    {
        double r = deg * Math.PI / 180, c = Math.Cos(r), sn = Math.Sin(r);
        (_vx, _vy) = (_vx * c - _vy * sn, _vx * sn + _vy * c);
    }

    /// <summary>
    /// 브레이크 슬라이드의 슬립각을 진행 방향으로 옮김 (진행 방향이 차체 쪽으로 돌고 슬립각은 그만큼 줄어듦).
    /// 차체가 화면에서 보이는 방향은 그대로다.
    /// </summary>
    private double CarrySlip(double maxStep)
    {
        double step = Math.Sign(SlipAngle) * Math.Min(Math.Abs(SlipAngle), maxStep);
        SlipAngle -= step;
        return step;
    }

    /// <summary>슬라이드를 끝내며 남은 슬립각을 차 방향에 합침 (거의 멈췄을 때)</summary>
    private void CommitSlide()
    {
        Heading = Normalize(Heading + SlipAngle);
        SlipAngle = 0;
        _manualDrifting = false;
        _slidePhase = 0;
    }

    /// <summary>슬립각을 목표 쪽으로 (커질 때는 빠르게, 0으로 돌아갈 때는 조금 천천히)</summary>
    private void UpdateManualSlip(double target, double dt)
    {
        double rate = Math.Abs(target) > Math.Abs(SlipAngle) && Math.Sign(target) != -Math.Sign(SlipAngle)
            ? SlipBuildRate : SlipRecoverRate;
        double step = rate * dt;
        SlipAngle = Math.Abs(target - SlipAngle) <= step ? target : SlipAngle + Math.Sign(target - SlipAngle) * step;
        _manualDrifting = Math.Abs(SlipAngle) > 3;
    }

    public void SetMode(DriveMode mode)
    {
        if (mode == Mode) return;
        CancelDrift();
        Mode = mode;
        Braking = Reversing = false;
        ResetBoost();
        _dir16.Reset();
        if (mode == DriveMode.Manual)
        {
            // 자율 주행하던 속도 그대로 넘겨받아 관성으로 이어지게
            double speed = SpeedLogical * _lastDpi / 96.0;
            double rad = Heading * Math.PI / 180;
            _vx = Math.Cos(rad) * speed;
            _vy = Math.Sin(rad) * speed;
            DisplayAngle = Heading;
        }
        else
        {
            _vx = _vy = 0; // 자율 주행은 차가 바라보는 방향으로 출발
        }
    }

    /// <summary>즉시 정지 (주차 등)</summary>
    public void Stop()
    {
        _vx = _vy = 0;
        Braking = Reversing = false;
        ResetBoost();
    }

    private void ResetBoost()
    {
        BoostStage = 0;
        Boosting = false;
        _boostTimer = 0;
        _launching = false;
        _tapCount = 0;
        BrakeSkidding = false;
    }

    /// <summary>
    /// 연타 부스트 감지. 가속 키를 LaunchTapSeconds 안에 연달아 누르면 두 번째부터 누를 때마다
    /// 부스터 단계가 하나씩 올라간다. 기준은 연타를 시작한 순간의 단계:
    /// 멈춘 상태(0단)에서 3연타 → 2단 출발, 2단으로 달리다 3연타 → 4단.
    /// </summary>
    /// <param name="forward">이 누름이 지금 진행 방향으로의 가속인지 (후진 중이거나 탑뷰에서 다른 방향이면 false)</param>
    private void UpdateLaunch(double dt, bool accelPressed, double speed, double baseMax, bool forward = true)
    {
        _sinceLastTap += dt;
        bool pressed = accelPressed && !_prevAccelPressed;
        _prevAccelPressed = accelPressed;
        if (!pressed) return;

        bool starting = speed < baseMax * 0.35;
        bool continuing = _tapCount >= 1 && _sinceLastTap <= LaunchTapSeconds;
        _sinceLastTap = 0;

        if (continuing && (forward || starting)) _tapCount++;
        else if (starting || forward)
        {
            // 연타 시작: 지금 단계를 기준으로 기억
            _tapCount = 1;
            _tapBaseStage = starting ? 0 : BoostStage;
            _tapFromStop = starting;
        }
        else
        {
            _tapCount = 0;
            return;
        }

        if (LaunchTapBoost && _tapCount >= 2 && BoostMultipliers.Length > 0)
        {
            BoostStage = Math.Min(_tapBaseStage + _tapCount - 1, BoostMultipliers.Length);
            _boostTimer = 0;
            _launching = true;
        }
    }

    /// <summary>가속력: 연타 출발 중이면 그 단계 최고 속도까지 AccelSeconds 정도에 닿도록 강하게</summary>
    private double AccelFor(double baseMax)
    {
        double a = baseMax / Math.Max(0.05, AccelSeconds);
        if (_launching) return a * StageMax(1, BoostStage);
        return BoostStage > 0 ? a * BoostAccelRatio : a;
    }

    /// <summary>단계별 최고 속도</summary>
    private double StageMax(double baseMax, int stage) =>
        stage <= 0 || BoostMultipliers.Length == 0 ? baseMax : baseMax * BoostMultipliers[Math.Min(stage, BoostMultipliers.Length) - 1];

    /// <summary>
    /// 부스트 단계 갱신. 가속 중 현재 단계 최고 속도 근처에 일정 시간 머무르면 올라가고,
    /// 속도가 아래 단계 최고 속도의 90% 밑으로 떨어지면 내려간다.
    /// </summary>
    private void UpdateBoost(double dt, double baseMax, double speed, bool accelerating)
    {
        if (_launching)
        {
            // 연타 출발: 가속 키를 누르고 있는 동안 단계를 유지, 그 단계 최고 속도에 닿으면 평소 규칙으로
            if (!accelerating || BoostStage == 0 || speed >= StageMax(baseMax, BoostStage) * 0.98) _launching = false;
            else
            {
                Boosting = true;
                return;
            }
        }

        int maxStage = BoostMultipliers.Length;
        if (accelerating && BoostStage < maxStage && speed >= StageMax(baseMax, BoostStage) * 0.98)
        {
            _boostTimer += dt;
            if (_boostTimer >= BoostHoldSeconds)
            {
                BoostStage++;
                _boostTimer = 0;
            }
        }
        else
        {
            _boostTimer = 0;
        }

        while (BoostStage > 0 && speed < StageMax(baseMax, BoostStage - 1) * 0.9) BoostStage--;
        Boosting = accelerating && BoostStage > 0;
    }

    // ───────────────────── 마우스 드래그 ─────────────────────

    /// <summary>드래그 중 위치 지정. 화면 밖이면 무시.</summary>
    public bool DragTo(double x, double y)
    {
        if (!_layout.Contains(x, y)) return false;
        CancelDrift();
        Braking = false;
        X = x;
        Y = y;
        _vx = _vy = 0;
        Braking = Reversing = false;
        _recheckOverlap = true; // 선 위에 내려놓아도 빠져나올 수 있게
        return true;
    }

    /// <summary>드래그를 놓을 때의 마우스 속도로 던지기 (물리 px/s)</summary>
    public void Throw(double vx, double vy, int dpi)
    {
        double max = SpeedLogical * dpi / 96.0;
        double speed = Math.Sqrt(vx * vx + vy * vy);
        if (speed < 120 * dpi / 96.0) return; // 살짝 움직인 건 던지기로 보지 않음

        CancelDrift();
        Heading = Normalize(Math.Atan2(vy, vx) * 180 / Math.PI);
        if (Style == ControlStyle.Car) DisplayAngle = Heading;
        if (Mode == DriveMode.Manual)
        {
            double s = Math.Min(speed, max);
            _vx = vx / speed * s;
            _vy = vy / speed * s;
        }
    }

    // ───────────────────── 갱신 ─────────────────────

    public void Update(double dt, int dpi, ManualInput input = default)
    {
        _lastDpi = dpi;
        double speed = SpeedLogical * dpi / 96.0;

        double bx = X, by = Y;
        if (Mode == DriveMode.Auto)
        {
            UpdateAuto(dt, speed);
            DisplayAngle = _drifting
                ? Normalize(Heading + SlipAngle)                         // 드리프트: 차체가 진행 방향보다 더 돌아감
                : Approach(DisplayAngle, Heading, AutoTurnRate * dt);
        }
        else if (Style == ControlStyle.Car)
        {
            UpdateCarStyle(dt, speed, input);
            DisplayAngle = Normalize(Heading + SlipAngle); // 자동차식은 차체 = 진행 방향 (+ 드리프트 슬립)
        }
        else
        {
            UpdateTopDown(dt, speed, input);
            DisplayAngle = _manualDrifting
                ? Normalize(Heading + SlipAngle)                            // 드리프트: 차체가 먼저 새 방향을 향함
                : Approach(DisplayAngle, Heading, TopDownTurnRate * dt);
        }

        if (Track != null && !Track.IsEmpty) ResolveTrack(bx, by, dt);

        // 속력: 수동은 속도 벡터 크기, 자율은 실제로 움직인 거리 (벽 반사 순간의 0을 피하려고 이전 값과 섞음)
        if (Mode == DriveMode.Manual) Speed = CurrentSpeed;
        else if (dt > 0)
        {
            double moved = Math.Sqrt((X - bx) * (X - bx) + (Y - by) * (Y - by)) / dt;
            Speed = moved > speed * 2.5 ? Speed : Speed + (moved - Speed) * Math.Min(1, dt * 20);
        }

        // 모니터가 분리되는 등으로 화면 밖에 남으면 주 모니터로 복귀
        if (!_layout.Contains(X, Y)) PlaceAtPrimaryCenter();
    }

    /// <summary>
    /// 트랙 선과의 충돌. 이번 프레임 이동으로 선에 더 깊이 파고들면 이동을 취소하고
    /// 자율 주행은 선에서 반사, 수동 주행은 선을 따라 미끄러지게 한다.
    /// 이미 겹쳐 있던 경우(차 위에 선을 그은 경우 등)는 빠져나오는 이동은 허용한다.
    /// </summary>
    private void ResolveTrack(double bx, double by, double dt)
    {
        UpdateGhostStrokes(bx, by);
        double pen = Track!.Penetration(X, Y, DisplayAngle, HalfLength, HalfWidth, out double nx, out double ny, _ghostStrokes);
        if (pen <= 0.05) return;
        double before = Track.Penetration(bx, by, DisplayAngle, HalfLength, HalfWidth, out _, out _, _ghostStrokes);
        if (pen <= before + 0.05) return;

        TrackHitCount++;
        X = bx;
        Y = by;

        if (Mode == DriveMode.Auto)
        {
            CancelDrift();
            double h = Heading * Math.PI / 180, dx = Math.Cos(h), dy = Math.Sin(h);
            double dot = dx * nx + dy * ny;
            if (dot < 0)
            {
                double rx = dx - 2 * dot * nx, ry = dy - 2 * dot * ny;
                double reflected = Math.Atan2(ry, rx) * 180 / Math.PI;
                double jittered = reflected + (_rng.NextDouble() * 2 - 1) * BounceJitterDegrees;
                double jr = jittered * Math.PI / 180;
                // 흔든 방향이 다시 선 쪽이거나 선과 거의 평행하면 순수 반사각
                Heading = Normalize(Math.Cos(jr) * nx + Math.Sin(jr) * ny > 0.15 ? jittered : reflected);
            }
            return;
        }

        // 수동: 선을 향한 속도 성분을 없애고 선을 따라 미끄러짐
        double vn = _vx * nx + _vy * ny;
        double mx = _vx, my = _vy; // 이번 프레임에 시도할 미끄러짐 이동 속도
        if (vn < 0)
        {
            mx = _vx - vn * nx;
            my = _vy - vn * ny;
            if (Style == ControlStyle.Car)
            {
                // 자동차식: 속도는 차체 방향이어야 하므로, 선과 이루는 각만큼 속도를 줄이고
                // 차체를 선 방향으로 조금씩 돌려 벽을 긁으며 따라가게 함
                double hr = Heading * Math.PI / 180, fx = Math.Cos(hr), fy = Math.Sin(hr);
                double s = _vx * fx + _vy * fy;
                double tx0 = -ny, ty0 = nx;
                double along = fx * tx0 + fy * ty0;
                if (along < 0) { tx0 = -tx0; ty0 = -ty0; along = -along; }
                if (s < 0) { tx0 = -tx0; ty0 = -ty0; } // 후진 중이면 뒤쪽 방향
                s *= along;
                double tangentDeg = Math.Atan2(s < 0 ? -ty0 : ty0, s < 0 ? -tx0 : tx0) * 180 / Math.PI;
                if (along > 0.05) Heading = Approach(Heading, tangentDeg, 150 * dt);
                hr = Heading * Math.PI / 180;
                _vx = s * Math.Cos(hr);
                _vy = s * Math.Sin(hr);
                _wallContact = true; // 다음 프레임에 핸들로 빠져나올 수 있게
            }
            else
            {
                _vx -= 1.25 * vn * nx; // 탑뷰: 살짝 튕김
                _vy -= 1.25 * vn * ny;
            }
        }

        // 선을 따라 미끄러지기 (더 파고들지 않을 때만)
        double tx = X + mx * dt, ty = Y + my * dt;
        if (Track.Penetration(tx, ty, DisplayAngle, HalfLength, HalfWidth, out _, out _, _ghostStrokes) <= before + 0.05 && _layout.Contains(tx, ty))
        {
            X = tx;
            Y = ty;
        }
    }

    /// <summary>새로 생긴 선이나 차를 옮긴 직후 차와 겹쳐 있는 선은 통과 대상으로, 벗어나면 다시 벽으로</summary>
    private void UpdateGhostStrokes(double bx, double by)
    {
        var strokes = Track!.Strokes;
        if (_knownStrokes.Count > strokes.Count * 2 + 16) _knownStrokes.IntersectWith(strokes);
        foreach (var s in strokes)
        {
            bool isNew = _knownStrokes.Add(s);
            if ((isNew || _recheckOverlap) && Track.StrokePenetration(s, bx, by, DisplayAngle, HalfLength, HalfWidth, out _, out _) > 0.5)
                _ghostStrokes.Add(s);
        }
        _recheckOverlap = false;
        if (_ghostStrokes.Count == 0) return;
        _ghostStrokes.RemoveWhere(s => !strokes.Contains(s) ||
            (Track.StrokePenetration(s, X, Y, DisplayAngle, HalfLength, HalfWidth, out _, out _) <= 0 &&
             Track.StrokePenetration(s, bx, by, DisplayAngle, HalfLength, HalfWidth, out _, out _) <= 0));
    }

    /// <summary>멈춤·드래그 중에는 속도 0으로 표시</summary>
    public void ClearSpeed() => Speed = 0;

    // ───────────────────── 자율 주행 ─────────────────────

    private void UpdateAuto(double dt, double speed)
    {
        if (DriftEnabled)
        {
            if (!_drifting) TryStartDrift(speed);
            if (_drifting)
            {
                DriftStep(dt, speed);
                return;
            }
        }
        Braking = false;

        double rad = Heading * Math.PI / 180;
        double vx = Math.Cos(rad) * speed;
        double vy = Math.Sin(rad) * speed;
        var (hx, hy) = Extents();

        double nx = X + vx * dt;
        double ny = Y + vy * dt;
        bool bouncedX = false, bouncedY = false;

        if (Math.Abs(vx) > 1e-6 && !EdgeFreeX(nx, Y, Math.Sign(vx), hx, hy))
        {
            vx = -vx; nx = X; bouncedX = true;
        }
        if (Math.Abs(vy) > 1e-6 && !EdgeFreeY(nx, ny, Math.Sign(vy), hx, hy))
        {
            vy = -vy; ny = Y; bouncedY = true;
        }
        // L자 배치의 안쪽 모서리처럼 축별로는 통과지만 대각선이 막힌 경우
        if (!bouncedX && !bouncedY && Math.Abs(vx) > 1e-6 && Math.Abs(vy) > 1e-6 &&
            !_layout.Contains(nx + Math.Sign(vx) * hx, ny + Math.Sign(vy) * hy))
        {
            vx = -vx; vy = -vy; nx = X; ny = Y; bouncedX = bouncedY = true;
        }

        X = nx;
        Y = ny;

        if (bouncedX || bouncedY)
        {
            WallHitCount++;
            Heading = ReflectedHeading(Heading, bouncedX, bouncedY);
        }
    }

    /// <summary>벽(축)에 대한 반사각 + 무작위 흔들기. 흔든 각도가 벽 쪽을 향하거나 벽과 거의 평행하면 순수 반사각.</summary>
    private double ReflectedHeading(double heading, bool flipX, bool flipY)
    {
        double rad = heading * Math.PI / 180;
        double vx = Math.Cos(rad) * (flipX ? -1 : 1);
        double vy = Math.Sin(rad) * (flipY ? -1 : 1);
        double reflected = Math.Atan2(vy, vx) * 180 / Math.PI;
        double jittered = reflected + (_rng.NextDouble() * 2 - 1) * BounceJitterDegrees;
        double jr = jittered * Math.PI / 180;
        bool ok = (!flipX || (Math.Sign(Math.Cos(jr)) == Math.Sign(vx) && Math.Abs(Math.Cos(jr)) > 0.15)) &&
                  (!flipY || (Math.Sign(Math.Sin(jr)) == Math.Sign(vy) && Math.Abs(Math.Sin(jr)) > 0.15));
        return Normalize(ok ? jittered : reflected);
    }

    // ───────────────────── 자율 주행: 드리프트 ─────────────────────
    //
    //  벽에 닿을 지점을 미리 찾고, 회전 반경 R = 속도 / 회전 속도 로 돌았을 때
    //  벽 쪽으로 더 나아가는 거리 R·(1 - cos β) (β = 벽과 이루는 각)가 남은 거리와 같아지는 순간 드리프트를 시작한다.
    //  진행 방향은 반사 방향까지 일정한 속도로 돌고, 차체는 그보다 더 돌아가(슬립각) 뒤가 바깥으로 미끄러진다.

    private void TryStartDrift(double speed)
    {
        double omega = DriftTurnDegreesPerSecond * Math.PI / 180;
        double radius = speed / omega;
        double margin = HalfWidth * 0.4;
        var hit = PredictWall(radius + margin + speed * 0.05 + 4);
        if (hit == null) return;
        var (dist, wallX, wallY) = hit.Value;

        double rad = Heading * Math.PI / 180;
        // 벽 쪽으로 더 나아가는 비율 (1 - cos β). 모서리(두 벽)는 정면 충돌처럼 취급
        double approach = wallX && wallY ? 1
            : wallX ? 1 - Math.Abs(Math.Sin(rad))
            : 1 - Math.Abs(Math.Cos(rad));
        double needed = radius * approach + margin;
        if (dist > needed + speed * (1 / 60.0)) return; // 아직 이름

        // 남은 거리로 필요한 최소 회전 속도 (늦게 감지했으면 더 급하게)
        double usable = Math.Max(2, dist - margin);
        double needOmega = approach > 1e-3 ? speed / Math.Max(4, usable / approach) : omega;
        double baseOmega = Math.Max(omega, needOmega) * 180 / Math.PI;

        // 후보 경로를 미리 따라가 보고 옆 벽(모서리)에 막히지 않는 첫 번째 것을 고른다
        double natural = ReflectedHeading(Heading, wallX, wallY);
        double uTurn = ReflectedHeading(Heading, true, true);
        int naturalDir = ShortestDir(Heading, natural);
        var plans = new List<(double target, int dir, double omega)>
        {
            (natural, naturalDir, baseOmega),
            (natural, naturalDir, baseOmega * 1.6),
            (natural, naturalDir, baseOmega * 2.5),
            (natural, -naturalDir, baseOmega * 1.6),
            (uTurn, naturalDir, baseOmega * 1.6),
            (uTurn, -naturalDir, baseOmega * 1.6),
            (uTurn, naturalDir, baseOmega * 3),
            (uTurn, -naturalDir, baseOmega * 3),
        };
        var chosen = plans[0];
        foreach (var plan in plans)
        {
            if (SimulateDrift(plan.target, plan.dir, plan.omega, speed))
            {
                chosen = plan;
                break;
            }
        }

        double total = TurnAmount(Heading, chosen.target, chosen.dir);
        if (total < 1) return;
        _driftDir = chosen.dir;
        _driftTotal = total;
        _driftTurned = 0;
        _driftTarget = chosen.target;
        _driftOmega = chosen.omega;
        _drifting = true;
        DriftCount++;
    }

    /// <summary>from에서 to로 가는 짧은 쪽 회전 방향 (정면 반대면 무작위)</summary>
    private int ShortestDir(double from, double to)
    {
        double diff = ((to - from) % 360 + 540) % 360 - 180;
        if (Math.Abs(diff) > 178) return _rng.Next(2) == 0 ? -1 : 1;
        return diff >= 0 ? 1 : -1;
    }

    /// <summary>from에서 dir 방향으로 돌아 to에 닿기까지의 각도 (0~360)</summary>
    private static double TurnAmount(double from, double to, int dir)
    {
        double d = dir > 0 ? to - from : from - to;
        d %= 360;
        return d < 0 ? d + 360 : d;
    }

    /// <summary>드리프트 계획을 실제로 움직이지 않고 미리 따라가 보며 벽에 막히는지 확인</summary>
    private bool SimulateDrift(double target, int dir, double omegaDeg, double speed)
    {
        double total = TurnAmount(Heading, target, dir);
        if (total < 1) return true;
        if (total > 270) return false; // 너무 많이 도는 쪽은 부자연스러움
        double x = X, y = Y, heading = Heading, turned = 0;
        const double dt = 1 / 120.0;
        double slipScale = Math.Min(1, total / 90);
        for (int i = 0; i < 2000 && turned < total - 1e-9; i++)
        {
            double progress = turned / total;
            double step = Math.Min(omegaDeg * dt, total - turned);
            turned += step;
            heading += dir * step;
            double slip = dir * DriftSlipDegrees * slipScale * Math.Sin(Math.PI * Math.Min(1, progress));
            double v = speed * (1 - 0.22 * Math.Sin(Math.PI * Math.Min(1, progress)));
            double rad = heading * Math.PI / 180;
            double vx = Math.Cos(rad) * v, vy = Math.Sin(rad) * v;
            var (hx, hy) = Extents(heading + slip);
            double nx = x + vx * dt, ny = y + vy * dt;
            if (Math.Abs(vx) > 1e-6 && !EdgeFreeX(nx, y, Math.Sign(vx), hx, hy)) return false;
            if (Math.Abs(vy) > 1e-6 && !EdgeFreeY(nx, ny, Math.Sign(vy), hx, hy)) return false;
            x = nx;
            y = ny;
        }
        // 다 돈 뒤 바로 앞이 막혀 있지 않아야 함
        double r = target * Math.PI / 180;
        var (ex, ey) = Extents(target);
        double fx = x + Math.Cos(r) * 6, fy = y + Math.Sin(r) * 6;
        return (Math.Abs(Math.Cos(r)) < 1e-6 || EdgeFreeX(fx, y, Math.Sign(Math.Cos(r)), ex, ey)) &&
               (Math.Abs(Math.Sin(r)) < 1e-6 || EdgeFreeY(fx, fy, Math.Sign(Math.Sin(r)), ex, ey));
    }

    private void DriftStep(double dt, double speed)
    {
        double progress = _driftTurned / _driftTotal;
        double turnStep = Math.Min(_driftOmega * dt, _driftTotal - _driftTurned);
        _driftTurned += turnStep;
        Heading = Normalize(Heading + _driftDir * turnStep);

        // 슬립각: 중간에 가장 크고 끝에서 0으로 (작게 꺾는 드리프트는 슬립도 작게)
        double slipScale = Math.Min(1, _driftTotal / 90);
        SlipAngle = _driftDir * DriftSlipDegrees * slipScale * Math.Sin(Math.PI * Math.Min(1, progress));
        Braking = progress < 0.45; // 진입하면서 브레이크

        // 속도: 드리프트 중간에 약 22% 감속했다 회복
        double v = speed * (1 - 0.22 * Math.Sin(Math.PI * Math.Min(1, progress)));
        double rad = Heading * Math.PI / 180;
        double vx = Math.Cos(rad) * v, vy = Math.Sin(rad) * v;
        var (hx, hy) = Extents();

        double nx = X + vx * dt, ny = Y + vy * dt;
        bool blockedX = Math.Abs(vx) > 1e-6 && !EdgeFreeX(nx, Y, Math.Sign(vx), hx, hy);
        if (blockedX) nx = X;
        bool blockedY = Math.Abs(vy) > 1e-6 && !EdgeFreeY(nx, ny, Math.Sign(vy), hx, hy);
        if (blockedY) ny = Y;
        X = nx;
        Y = ny;

        if (blockedX || blockedY)
        {
            // 안전장치: 그래도 벽에 닿으면 즉시 반사하고 드리프트 종료
            WallHitCount++;
            CancelDrift();
            Braking = false;
            Heading = ReflectedHeading(Heading, blockedX, blockedY);
            return;
        }

        if (_driftTurned >= _driftTotal - 1e-9)
        {
            _drifting = false;
            SlipAngle = 0;
            Braking = false;
            Heading = _driftTarget;
        }
    }

    /// <summary>진행 방향으로 maxDist까지 앞을 살펴 처음 막히는 거리와 막힌 축</summary>
    internal (double dist, bool wallX, bool wallY)? PredictWall(double maxDist)
    {
        double rad = Heading * Math.PI / 180;
        double ux = Math.Cos(rad), uy = Math.Sin(rad);
        var (hx, hy) = Extents();
        const double step = 4;
        for (double d = step; d <= maxDist; d += step)
        {
            double nx = X + ux * d, ny = Y + uy * d;
            bool bx = Math.Abs(ux) > 1e-6 && !EdgeFreeX(nx, ny, Math.Sign(ux), hx, hy);
            bool by = Math.Abs(uy) > 1e-6 && !EdgeFreeY(nx, ny, Math.Sign(uy), hx, hy);
            if (!bx && !by && Math.Abs(ux) > 1e-6 && Math.Abs(uy) > 1e-6 &&
                !_layout.Contains(nx + Math.Sign(ux) * hx, ny + Math.Sign(uy) * hy))
                bx = by = true; // L자 안쪽 모서리
            if (bx || by) return (d - step, bx, by);
        }
        return null;
    }

    // ───────────────────── 수동: 자동차식 ─────────────────────

    private void UpdateCarStyle(double dt, double maxSpeed, ManualInput k)
    {
        double rad = Heading * Math.PI / 180;
        double fx = Math.Cos(rad), fy = Math.Sin(rad);

        // 차체 방향 성분만 남김 (자동차는 옆으로 가지 않음)
        double s = _vx * fx + _vy * fy;

        double baseMax = maxSpeed;
        UpdateLaunch(dt, k.Up && !k.Down && !k.Brake, Math.Abs(s), baseMax, forward: s >= 0);
        maxSpeed = StageMax(baseMax, BoostStage);
        double accel = AccelFor(baseMax);
        double brake = baseMax / Math.Max(0.05, BrakeSeconds);
        double coast = baseMax / Math.Max(0.05, CoastSeconds);
        bool braking = false;
        int steer = (k.Right ? 1 : 0) - (k.Left ? 1 : 0);

        // 브레이크 슬라이드: 전진하며 브레이크 + 핸들 → 시작, 브레이크를 떼면 접지 회복
        if (_slidePhase != 1 && BrakeSlide && k.Brake && steer != 0 && s > baseMax * SlideStartRatio) _slidePhase = 1;
        else if (_slidePhase == 1 && !k.Brake) _slidePhase = 2;

        if (k.Brake)
        {
            s = Toward0(s, brake * (_slidePhase == 1 ? SlideBrakeRatio : 1) * dt);
            braking = true;
        }
        else if (k.Up && !k.Down)
        {
            if (s < 0) { s = Toward0(s, brake * dt); braking = true; } // 후진 중 ↑ = 브레이크
            else s = Math.Min(maxSpeed, s + accel * dt);
        }
        else if (k.Down && !k.Up)
        {
            if (s > 0) { s = Toward0(s, brake * dt); braking = true; } // 전진 중 ↓ = 브레이크
            else s = Math.Max(-baseMax * ReverseRatio, s - baseMax / Math.Max(0.05, AccelSeconds) * ReverseRatio * dt); // 멈춰 있으면 후진
        }
        else
        {
            s = Toward0(s, coast * dt);
        }

        // 최고 속도 단계를 낮췄을 때
        if (s > maxSpeed) s = Math.Max(maxSpeed, s - coast * 2 * dt);
        if (s < -baseMax * ReverseRatio) s = Math.Min(-baseMax * ReverseRatio, s + coast * 2 * dt);

        UpdateBoost(dt, baseMax, s, k.Up && !k.Down && !k.Brake && s >= 0);

        // 핸들: 움직일 때만 돌고, 느릴수록 덜 돎. 후진 시에는 반대로 돎 (실제 차처럼)
        if (_slidePhase != 0)
        {
            UpdateCarSlide(dt, ref s, steer, baseMax);
            rad = Heading * Math.PI / 180;
            fx = Math.Cos(rad); fy = Math.Sin(rad);
        }
        else
        {

        // 부스트 드리프트: 부스트 중(또는 이미 미끄러지는 중) 전진하며 핸들을 꺾으면,
        // 차체가 진행 방향보다 꺾는 쪽으로 더 돌아가고(슬립각) 진행 방향은 조금 덜 돈다.
        bool canDrift = BoostDrift && !k.Brake && s > baseMax * 0.6 && (BoostStage > 0 || _manualDrifting);
        double targetSlip = canDrift && steer != 0 ? steer * DriftSlipDegrees * Math.Min(1, s / (baseMax * 1.2)) : 0;
        UpdateManualSlip(targetSlip, dt);
        if (_manualDrifting)
            s = Math.Max(0, s - baseMax * 0.12 * Math.Abs(SlipAngle) / Math.Max(1, DriftSlipDegrees) * dt); // 미끄러지며 약간 감속

        if (steer != 0)
        {
            double grip = Math.Min(1, Math.Abs(s) / (baseMax * 0.3));
            int dirSign = Math.Sign(s);
            // 벽에 붙어 속도가 거의 없을 때 ↑/↓를 누르고 있으면 핸들이 천천히 돌아 빠져나올 수 있게
            if (_wallContact && (k.Up || k.Down) && grip < 0.35)
            {
                grip = 0.35;
                dirSign = k.Down && !k.Up ? -1 : 1;
            }
            double turn = SteeringDegreesPerSecond * grip * (_manualDrifting ? 0.85 : 1);
            Heading = Normalize(Heading + steer * turn * dirSign * dt);
            rad = Heading * Math.PI / 180;
            fx = Math.Cos(rad); fy = Math.Sin(rad);
        }
        }

        _vx = s * fx;
        _vy = s * fy;
        Braking = braking && Math.Abs(s) > 0.5;
        Reversing = s < -0.5;
        BrakeSkidding = braking && Math.Abs(s) > baseMax * 0.2; // 빠를 때 밟으면 자국

        // 이동 + 벽: 막힌 축은 멈추고, 벽에 비스듬히 닿으면 벽을 따라 미끄러지며 감속
        var (hx, hy) = Extents();
        double nx = X + _vx * dt;
        bool blockedX = _vx != 0 && !EdgeFreeX(nx, Y, Math.Sign(_vx), hx, hy);
        if (blockedX) nx = X;
        double ny = Y + _vy * dt;
        bool blockedY = _vy != 0 && !EdgeFreeY(nx, ny, Math.Sign(_vy), hx, hy);
        if (blockedY) ny = Y;

        if (blockedX && blockedY) s = 0;
        else if (blockedX) s *= Math.Abs(fy);
        else if (blockedY) s *= Math.Abs(fx);
        if (blockedX || blockedY) { _vx = s * fx; _vy = s * fy; }
        _wallContact = blockedX || blockedY || ((k.Up || k.Down) && Math.Abs(s) < baseMax * 0.02 && WallAhead(k.Down && !k.Up));

        X = nx;
        Y = ny;
    }

    /// <summary>
    /// 자동차식 브레이크 슬라이드. Heading = 실제로 미끄러져 가는 방향, SlipAngle = 차체가 더 돌아간 각도.
    /// 미끄러지는 중: 핸들 쪽으로 차체가 빠르게 돌고(최대 BrakeSlideDegrees), 진행 방향은 관성으로 거의 그대로.
    /// 브레이크를 떼면: 바퀴가 다시 잡히며 진행 방향이 차체 쪽으로 돌아 정렬 (속도를 조금 잃음).
    /// </summary>
    private void UpdateCarSlide(double dt, ref double s, int steer, double baseMax)
    {
        if (_slidePhase == 1)
        {
            if (steer != 0)
            {
                double yaw = SlideYawRate * Math.Min(1, s / (baseMax * 0.5));
                SlipAngle = MathX.Clamp(SlipAngle + steer * yaw * dt, -BrakeSlideDegrees, BrakeSlideDegrees);
            }
            Heading = Normalize(Heading + CarrySlip(SlideCarryRate * dt));
        }
        else
        {
            double step = CarrySlip(SlipRecoverRate * dt);
            Heading = Normalize(Heading + step);
            s = Math.Max(0, s - baseMax * 0.4 * Math.Abs(step) / 90); // 다시 잡히며 속도 손실
            if (SlipAngle == 0) _slidePhase = 0;
        }
        _manualDrifting = Math.Abs(SlipAngle) > 3;
        if (s < baseMax * 0.04) CommitSlide(); // 거의 멈춤: 차체가 돌아간 방향 그대로 정지
    }

    // ───────────────────── 수동: 탑뷰 게임식 (16방향) ─────────────────────

    private void UpdateTopDown(double dt, double maxSpeed, ManualInput k)
    {
        double? target = _dir16.Update(k, dt);
        bool input = target.HasValue;
        double ux = 0, uy = 0;
        if (input)
        {
            double tr = target!.Value * Math.PI / 180;
            ux = Math.Cos(tr);
            uy = Math.Sin(tr);
        }

        double baseMax = maxSpeed;
        // 탑뷰: 달리는 중에는 지금 진행 방향(±30°)의 방향키 연타만 부스트로 봄 (옆 방향 톡 조향과 구분)
        bool forward = true;
        if (input && CurrentSpeed > 1)
        {
            double velDeg = Math.Atan2(_vy, _vx) * 180 / Math.PI;
            forward = Math.Abs(((target!.Value - velDeg) % 360 + 540) % 360 - 180) <= 30;
        }
        UpdateLaunch(dt, input && !k.Brake, CurrentSpeed, baseMax, forward);
        maxSpeed = StageMax(baseMax, BoostStage);

        // 부스트 드리프트: 부스트 중 다른 방향을 고르면 차체는 먼저 그쪽을 향하고(슬립각),
        // 접지력이 낮아져 실제 진행 방향은 미끄러지며 천천히 따라간다.
        double speedNow = CurrentSpeed;
        double velAngle = speedNow > 1 ? Math.Atan2(_vy, _vx) * 180 / Math.PI : Heading;

        // 브레이크 슬라이드: 달리며 브레이크 + 진행 방향과 다른 방향키 → 차체는 그쪽으로 돌고 몸은 관성으로 미끄러짐
        double inputDiff = input ? AngleDiff(target!.Value, velAngle) : 0;
        if (_slidePhase != 1 && BrakeSlide && k.Brake && input && speedNow > baseMax * SlideStartRatio && Math.Abs(inputDiff) > 15)
            _slidePhase = 1;
        else if (_slidePhase == 1 && !k.Brake) _slidePhase = 2;

        if (_slidePhase == 1)
        {
            if (input)
            {
                double want = MathX.Clamp(inputDiff, -BrakeSlideDegrees, BrakeSlideDegrees);
                double step = SlideYawRate * Math.Min(1, speedNow / (baseMax * 0.5)) * dt;
                SlipAngle = Math.Abs(want - SlipAngle) <= step ? want : SlipAngle + Math.Sign(want - SlipAngle) * step;
            }
            RotateVelocity(CarrySlip(SlideCarryRate * dt));
            _manualDrifting = Math.Abs(SlipAngle) > 3;
        }
        else if (_slidePhase == 2)
        {
            double step = CarrySlip(SlipRecoverRate * dt);
            RotateVelocity(step);
            Decelerate(baseMax * 0.4 * Math.Abs(step) / 90);
            _manualDrifting = Math.Abs(SlipAngle) > 3;
            if (SlipAngle == 0) _slidePhase = 0;
        }
        else
        {
            bool canDrift = BoostDrift && speedNow > baseMax * 0.6 && (BoostStage > 0 || _manualDrifting) && !k.Brake;
            double targetSlip = 0;
            if (canDrift && input)
            {
                double diff = AngleDiff(target!.Value, velAngle);
                if (Math.Abs(diff) > 8) targetSlip = Math.Max(-DriftSlipDegrees, Math.Min(DriftSlipDegrees, diff));
            }
            UpdateManualSlip(targetSlip, dt);
        }
        double grip = _manualDrifting ? DriftGrip : Grip;

        if (k.Brake)
        {
            Decelerate(baseMax / Math.Max(0.05, BrakeSeconds) * (_slidePhase == 1 ? SlideBrakeRatio : 1) * dt);
        }
        else if (input)
        {
            // 누르고 있는 동안 입력 방향으로 계속 가속
            double accel = AccelFor(baseMax);
            _vx += ux * accel * dt;
            _vy += uy * accel * dt;

            // 접지력: 입력 방향과 수직인 성분(옆미끄러짐)을 줄여서 방향 전환이 자연스럽게
            double along = _vx * ux + _vy * uy;
            double px = _vx - along * ux, py = _vy - along * uy;
            double keep = Math.Exp(-grip * dt);
            _vx = along * ux + px * keep;
            _vy = along * uy + py * keep;

            double s = CurrentSpeed;
            if (s > maxSpeed) { _vx *= maxSpeed / s; _vy *= maxSpeed / s; }
        }
        else
        {
            Decelerate(baseMax / Math.Max(0.05, CoastSeconds) * dt);
        }

        double cur = CurrentSpeed;
        if (cur > maxSpeed * 1.001) Decelerate(Math.Min(cur - maxSpeed, baseMax / Math.Max(0.05, CoastSeconds) * dt * 2));

        // 고른 방향으로 거의 곧게 달리고 있을 때만 부스트 (방향 전환 중에는 쌓이지 않음)
        double alongInput = input ? _vx * ux + _vy * uy : CurrentSpeed;
        UpdateBoost(dt, baseMax, alongInput, input && !k.Brake);

        Braking = k.Brake && CurrentSpeed > 0.5;
        BrakeSkidding = k.Brake && CurrentSpeed > baseMax * 0.2;
        Reversing = false;

        // 앞부분 방향: 움직이는 중이면 진행 방향, 거의 멈춰 있으면 고른 방향
        if (_slidePhase != 0 && CurrentSpeed <= baseMax * 0.06)
        {
            CommitSlide(); // 거의 멈춤: 차체가 돌아간 방향 그대로 정지
            DisplayAngle = Heading;
        }
        if (CurrentSpeed > baseMax * 0.06) Heading = Normalize(Math.Atan2(_vy, _vx) * 180 / Math.PI);
        else if (input && !k.Brake) Heading = target!.Value;

        var (hx, hy) = Extents();
        double nx = X + _vx * dt;
        if (_vx != 0 && !EdgeFreeX(nx, Y, Math.Sign(_vx), hx, hy)) { _vx = 0; nx = X; }
        double ny = Y + _vy * dt;
        if (_vy != 0 && !EdgeFreeY(nx, ny, Math.Sign(_vy), hx, hy)) { _vy = 0; ny = Y; }

        X = nx;
        Y = ny;
    }

    /// <summary>차 앞(또는 뒤) 바로 앞이 막혀 있는지</summary>
    private bool WallAhead(bool backward)
    {
        double r = (Heading + (backward ? 180 : 0)) * Math.PI / 180;
        double ux = Math.Cos(r), uy = Math.Sin(r);
        var (hx, hy) = Extents();
        double nx = X + ux * 3, ny = Y + uy * 3;
        return (Math.Abs(ux) > 0.05 && !EdgeFreeX(nx, Y, Math.Sign(ux), hx, hy)) ||
               (Math.Abs(uy) > 0.05 && !EdgeFreeY(X, ny, Math.Sign(uy), hx, hy));
    }

    /// <summary>방향은 유지하고 속력만 amount만큼 줄임</summary>
    private void Decelerate(double amount)
    {
        double s = CurrentSpeed;
        if (s <= amount || s < 1e-9) { _vx = _vy = 0; return; }
        double k = (s - amount) / s;
        _vx *= k;
        _vy *= k;
    }

    private static double Toward0(double v, double amount) =>
        Math.Abs(v) <= amount ? 0 : v - Math.Sign(v) * amount;

    // ───────────────────── 경계 판정 ─────────────────────

    /// <summary>현재 표시 각도 기준 회전된 차의 축별 반경</summary>
    private (double hx, double hy) Extents() => Extents(DisplayAngle);

    private (double hx, double hy) Extents(double angleDeg)
    {
        double a = angleDeg * Math.PI / 180;
        double c = Math.Abs(Math.Cos(a)), s = Math.Abs(Math.Sin(a));
        return (c * HalfLength + s * HalfWidth, s * HalfLength + c * HalfWidth);
    }

    /// <summary>X 방향으로 움직일 때 앞쪽 가장자리(위·가운데·아래)가 모두 화면 안인가</summary>
    private bool EdgeFreeX(double nx, double y, int sign, double hx, double hy)
    {
        double ex = nx + sign * hx;
        return _layout.Contains(ex, y) && _layout.Contains(ex, y - hy * 0.7) && _layout.Contains(ex, y + hy * 0.7);
    }

    private bool EdgeFreeY(double x, double ny, int sign, double hx, double hy)
    {
        double ey = ny + sign * hy;
        return _layout.Contains(x, ey) && _layout.Contains(x - hx * 0.7, ey) && _layout.Contains(x + hx * 0.7, ey);
    }

    private static double Normalize(double deg)
    {
        deg %= 360;
        return deg < 0 ? deg + 360 : deg;
    }

    /// <summary>최단 회전 방향으로 step만큼 target에 접근</summary>
    private static double Approach(double current, double target, double step)
    {
        double diff = ((target - current) % 360 + 540) % 360 - 180;
        if (Math.Abs(diff) <= step) return Normalize(target);
        return Normalize(current + Math.Sign(diff) * step);
    }
}
