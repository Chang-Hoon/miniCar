namespace miniCar;

/// <summary>
/// 자율 주행 · 트랙 따라 달리기.
/// 트랙이 있으면 차에서 가장 가까운 선 하나를 골라 그 선 옆(선 굵기 + 차 폭만큼 띄운 길)을 따라 달린다.
///  - 닫힌 선(시작과 끝이 맞닿음): 차가 있는 쪽(안쪽/바깥쪽)을 계속 돈다.
///  - 열린 선: 한쪽 옆을 따라가다 선 끝에서 U턴해 반대쪽 옆으로 돌아온다 (선을 감싸는 길).
/// 다른 선이나 화면 가장자리에 막히면 그 자리에서 방향을 바꿔(U턴) 되돌아간다.
/// </summary>
internal sealed partial class CarController
{
    /// <summary>자율 주행에서 트랙이 있으면 트랙을 따라 달림 (false면 예전처럼 화면 전체를 돌아다님)</summary>
    public bool FollowTrack { get; set; } = true;

    /// <summary>지금 트랙을 따라 달리는 중인지 (계기판 표시용)</summary>
    public bool FollowingTrack => Mode == DriveMode.Auto && _fPath != null;

    private const double FollowTurnRate = 300;      // 따라 달릴 때 최대 회전 속도 (°/s)
    private const double FollowStep = 6;            // 길 점 간격 (px)

    /// <summary>트랙을 따라 달리다 코너(방향 전환)에서 부스터를 켜고 드리프트 (BoostDrift 설정)</summary>
    public bool FollowBoostDrift { get; set; } = true;
    private const double CornerLook = 110;          // 이만큼 앞의 길 방향과 비교해 코너 판단 (px)
    private const double CornerMinBend = 40;        // 이 각도 이상 꺾이면 코너 (°)
    private const double CornerHoldSeconds = 0.35;  // 코너를 지나도 잠깐 부스터 유지
    private const double CornerTurnRate = 480;      // 드리프트 중 최대 회전 속도 (°/s)
    private double _fCornerHold;
    private int _fCornerStage, _fCornerSign = 1;

    /// <summary>테스트용: 코너에서 부스터 드리프트를 시작한 횟수</summary>
    internal int FollowBoostCount;

    private List<PointF>? _fPath;   // 따라갈 길 (닫힌 고리)
    private double[] _fCum = Array.Empty<double>();
    private double _fLen;
    private double _fS;             // 길 위 현재 위치 (길이)
    private int _fDir = 1;          // 길을 도는 방향 (+1 / -1)
    private int _fVersion = -1;
    private bool _fNeedPick = true;
    private double _fSpeed;
    private bool _followDrifting;

    /// <summary>테스트용: 막혀서 되돌아간 횟수</summary>
    internal int FollowReverseCount;

    /// <summary>차를 옮기거나 모드를 바꾸면 따라갈 선을 다시 고름</summary>
    private void RequestFollowRepick() => _fNeedPick = true;

    private void StopFollowing()
    {
        if (_fPath == null && !_followDrifting) return;
        _fPath = null;
        _fCornerHold = 0;
        if (Mode == DriveMode.Auto) { BoostStage = 0; Boosting = false; }
        if (_followDrifting)
        {
            DisplayAngle = Normalize(Heading + SlipAngle);
            SlipAngle = 0;
            _followDrifting = false;
        }
    }

    /// <returns>트랙을 따라 움직였으면 true (false면 평소 자율 주행)</returns>
    private bool UpdateFollow(double dt, double speed)
    {
        if (!FollowTrack || Track == null || Track.IsEmpty)
        {
            StopFollowing();
            return false;
        }

        bool global = false;
        if (_fPath == null || _fNeedPick || Track.Version != _fVersion)
        {
            if (_drifting) { DisplayAngle = Normalize(Heading + SlipAngle); _drifting = false; SlipAngle = 0; }
            BuildFollowPath();
            global = true;
            if (_fPath == null) return false;
        }
        var path = _fPath!;

        // 길 위 가장 가까운 위치 (평소에는 지난 위치 근처만 찾아서 반대쪽 길로 튀지 않게)
        double d = FollowOffset(_fStrokeWidth);
        double look = Math.Max(30, d * 1.2);
        double s = global ? ClosestOnPath(X, Y, 0, _fLen) : ClosestOnPath(X, Y, _fS - look * 3, _fS + look * 3);
        if (global)
        {
            // 지금 바라보는 방향과 맞는 쪽으로 돌기
            var (tx, ty) = TangentAt(s);
            double h = Heading * Math.PI / 180;
            _fDir = Math.Cos(h) * tx + Math.Sin(h) * ty >= 0 ? 1 : -1;
            _fSpeed = Math.Max(_fSpeed, speed * 0.5);
        }
        _fS = s;

        // 코너 감지: 앞쪽 길이 크게 꺾이면 부스터를 켜고 드리프트로 돎
        var (c0x, c0y) = TangentAt(s);
        var (c1x, c1y) = TangentAt(s + _fDir * CornerLook);
        double cornerBend = AngleDiff(Math.Atan2(c1y, c1x) * 180 / Math.PI, Math.Atan2(c0y, c0x) * 180 / Math.PI);
        _fReverseCooldown = Math.Max(0, _fReverseCooldown - dt);
        bool cornerAhead = FollowBoostDrift && DriftEnabled && Math.Abs(cornerBend) >= CornerMinBend
                           && _fReverseCooldown <= 0 && _fSpeed > speed * 0.45;
        if (cornerAhead)
        {
            int stage = Math.Abs(cornerBend) >= 130 ? 3 : Math.Abs(cornerBend) >= 80 ? 2 : 1;
            if (_fCornerHold <= 0) { _fCornerStage = 0; FollowBoostCount++; }
            _fCornerStage = Math.Max(_fCornerStage, Math.Min(stage, BoostMultipliers.Length));
            _fCornerSign = Math.Sign(cornerBend);
            _fCornerHold = CornerHoldSeconds;
        }
        else _fCornerHold = Math.Max(0, _fCornerHold - dt);
        bool corner = _fCornerHold > 0 && _fCornerStage > 0;
        BoostStage = corner ? _fCornerStage : 0;
        Boosting = corner;
        double turnLimit = corner ? CornerTurnRate : FollowTurnRate;

        // 앞쪽 목표점을 향해 핸들 (pure pursuit)
        var target = PointAt(s + _fDir * look);
        double desired = Math.Atan2(target.Y - Y, target.X - X) * 180 / Math.PI;
        double diff = AngleDiff(desired, Heading);
        double maxStep = turnLimit * dt;
        double turn = Math.Max(-maxStep, Math.Min(maxStep, diff));
        Heading = Normalize(Heading + turn);

        // 굽은 곳·크게 돌아야 할 때는 감속: 앞쪽 150px 안의 굽은 곳들을 보고, 그 지점까지 감속할 수 있는 속도로 제한
        double decel = speed * 4;
        double vCap = double.MaxValue;
        for (int k = 0; k <= 6; k++)
        {
            double sk = s + _fDir * k * 25;
            var (ax, ay) = TangentAt(sk);
            var (bx, by) = TangentAt(sk + _fDir * 50);
            double bendK = Math.Abs(AngleDiff(Math.Atan2(by, bx) * 180 / Math.PI, Math.Atan2(ay, ax) * 180 / Math.PI)) * Math.PI / 180;
            if (bendK < 1e-4) continue;
            double capK = (turnLimit * Math.PI / 180) / (bendK / 50);
            vCap = Math.Min(vCap, Math.Sqrt(capK * capK + 2 * decel * k * 25));
        }
        if (Math.Abs(diff) > 90) vCap = Math.Min(vCap, speed * 0.08); // 되돌아갈 때: 거의 제자리에서 돌기
        else if (Math.Abs(diff) > 60) vCap = Math.Min(vCap, speed * 0.35);
        // 코너에서는 부스터 배율만큼 빠르게 (돌 수 있는 속도 안에서)
        double top = corner ? speed * BoostMultipliers[_fCornerStage - 1] : speed;
        double vTarget = Math.Min(top, Math.Max(Math.Min(speed * 0.1, 40), vCap));
        double rate = vTarget < _fSpeed ? speed * 4 : speed * 1.5;
        _fSpeed = _fSpeed < vTarget ? Math.Min(vTarget, _fSpeed + rate * dt) : Math.Max(vTarget, _fSpeed - rate * dt);

        // 이동 (화면 가장자리에 막히면 되돌아감)
        double r = Heading * Math.PI / 180;
        double vx = Math.Cos(r) * _fSpeed, vy = Math.Sin(r) * _fSpeed;
        var (hx, hy) = Extents();
        double nx = X + vx * dt, ny = Y + vy * dt;
        bool blocked = (Math.Abs(vx) > 1e-6 && !EdgeFreeX(nx, Y, Math.Sign(vx), hx, hy)) ||
                       (Math.Abs(vy) > 1e-6 && !EdgeFreeY(nx, ny, Math.Sign(vy), hx, hy));
        if (blocked) ReverseFollow();
        else { X = nx; Y = ny; }
        Braking = false;

        // 빠르게 크게 돌 때는 뒤가 미끄러지는 모습 (타이어 자국)
        // 짧게 흔들리는 핸들은 무시하도록 회전 속도를 부드럽게 (0.2초)
        double turnRate = dt > 0 ? turn / dt : 0;
        _fTurnRate += (turnRate - _fTurnRate) * Math.Min(1, dt / 0.2);
        double wantSlip =
            corner && _fSpeed > speed * 0.45
                // 부스터 드리프트: 꺾이는 정도만큼 차체가 안쪽으로 돌아감 (최대 DriftSlipDegrees)
                ? _fCornerSign * DriftSlipDegrees * (0.55 + 0.45 * Math.Min(1, Math.Abs(cornerBend) / 120))
            : _fSpeed > speed * 0.55 && Math.Abs(_fTurnRate) > 150
                ? Math.Sign(_fTurnRate) * Math.Min(DriftSlipDegrees, (Math.Abs(_fTurnRate) - 150) * 0.18)
            : 0;
        double slipStep = (Math.Abs(wantSlip) > Math.Abs(SlipAngle) ? SlipBuildRate : SlipRecoverRate) * dt;
        SlipAngle = Math.Abs(wantSlip - SlipAngle) <= slipStep ? wantSlip : SlipAngle + Math.Sign(wantSlip - SlipAngle) * slipStep;
        _followDrifting = DriftEnabled && Math.Abs(SlipAngle) > 6;
        if (!DriftEnabled) SlipAngle = 0;
        return true;
    }

    /// <summary>막혔을 때: 길을 도는 방향을 바꿔 되돌아감</summary>
    private void ReverseFollow()
    {
        _fSpeed = 0;
        if (_fReverseCooldown > 0) return; // 방금 돌아섰으면 다시 뒤집지 않고 그 자리에서 마저 돎
        _fDir = -_fDir;
        _fReverseCooldown = 0.8;
        FollowReverseCount++;
    }

    private double _fReverseCooldown;
    private double _fTurnRate;

    private float _fStrokeWidth = 8;

    /// <summary>선 중심에서 차 중심까지 거리: 선 반굵기 + 차 반폭 + 끝에서 U턴할 때 차 앞뒤가 선에 닿지 않을 여유</summary>
    private double FollowOffset(float strokeWidth) => strokeWidth / 2 + HalfWidth + HalfLength * 0.9 + 4;

    // ───────────────────── 길 만들기 ─────────────────────

    private void BuildFollowPath()
    {
        _fNeedPick = false;
        _fVersion = Track!.Version;
        _fPath = null;

        // 차에서 가장 가까운 선
        TrackStroke? best = null;
        double bestD = double.MaxValue;
        foreach (var st in Track.Strokes)
        {
            double dd = st.Distance(X, Y, out _, out _) - st.Width / 2;
            if (dd < bestD) { bestD = dd; best = st; }
        }
        if (best == null) return;

        _fStrokeWidth = best.Width;
        double off = FollowOffset(best.Width);
        var pts = Resample(best.Points, FollowStep);
        bool closed = pts.Count >= 6 && Dist(best.Points[0], best.Points[best.Points.Count - 1]) <= Math.Max(24, best.Width * 3);
        if (closed && Dist(pts[0], pts[pts.Count - 1]) < FollowStep * 1.5) pts.RemoveAt(pts.Count - 1);
        Smooth(pts, closed, 3);

        List<PointF> path;
        if (closed)
        {
            // 차가 있는 쪽으로 띄운 고리
            int iNear = 0; double nd = double.MaxValue;
            for (int i = 0; i < pts.Count; i++) { double dd = Dist(pts[i], new PointF((float)X, (float)Y)); if (dd < nd) { nd = dd; iNear = i; } }
            var (nx0, ny0) = Normal(pts, iNear, true);
            int side = (X - pts[iNear].X) * nx0 + (Y - pts[iNear].Y) * ny0 >= 0 ? 1 : -1;
            path = Offset(pts, off * side, true);
        }
        else
        {
            // 선을 감싸는 길: 한쪽 옆 → 끝에서 반원 U턴 → 반대쪽 옆 → 시작에서 반원 U턴
            path = new List<PointF>();
            if (pts.Count == 1) pts.Add(new PointF(pts[0].X + 0.5f, pts[0].Y));
            // 끝을 감쌀 때는 선 끝을 지나 차 길이만큼 더 나간 뒤 반원으로 돌아서, 끝을 크게 돌아 나가게 함
            double ext = HalfLength * 1.6;
            var (ex, ey) = Tangent(pts, pts.Count - 1, false);
            var (sx, sy) = Tangent(pts, 0, false);
            var endTip = new PointF(pts[pts.Count - 1].X + (float)(ex * ext), pts[pts.Count - 1].Y + (float)(ey * ext));
            var startTip = new PointF(pts[0].X - (float)(sx * ext), pts[0].Y - (float)(sy * ext));
            path.AddRange(Offset(pts, off, false));
            AddCap(path, endTip, (ex, ey), off);
            var back = Offset(pts, -off, false);
            back.Reverse();
            path.AddRange(back);
            AddCap(path, startTip, (-sx, -sy), off);
        }

        // 너무 가까운 점 정리 + 누적 길이.
        // 안쪽으로 꺾인 모서리에서는 띄운 길이 반대쪽 변에 너무 붙거나 꼬이므로(제비꼬리 모양),
        // 원래 선에서 충분히 떨어지지 않은 점은 버린다.
        var clean = new List<PointF>();
        foreach (var p in path)
        {
            if (best.Distance(p.X, p.Y, out _, out _) < off * 0.92) continue;
            if (clean.Count == 0 || Dist(clean[clean.Count - 1], p) >= 1.5) clean.Add(p);
        }
        if (clean.Count < 3) return;
        _fPath = clean;
        _fCum = new double[clean.Count + 1];
        for (int i = 0; i < clean.Count; i++) _fCum[i + 1] = _fCum[i] + Dist(clean[i], clean[(i + 1) % clean.Count]);
        _fLen = _fCum[clean.Count];
    }

    /// <summary>center를 중심으로 진행 방향 t 쪽으로 볼록한 반원 (왼쪽 옆 → 오른쪽 옆)</summary>
    private static void AddCap(List<PointF> path, PointF center, (double x, double y) t, double r)
    {
        double a0 = Math.Atan2(t.x, -t.y); // 법선 (-ty, tx) 방향의 각도
        for (int i = 0; i <= 18; i++)
        {
            double a = a0 - Math.PI * i / 18; // 법선 → 진행 방향 → 반대 법선
            path.Add(new PointF(center.X + (float)(Math.Cos(a) * r), center.Y + (float)(Math.Sin(a) * r)));
        }
    }

    private static List<PointF> Resample(IReadOnlyList<PointF> src, double step)
    {
        var res = new List<PointF> { src[0] };
        double carry = 0;
        for (int i = 0; i + 1 < src.Count; i++)
        {
            PointF a = src[i], b = src[i + 1];
            double len = Dist(a, b);
            if (len < 1e-6) continue;
            double t = step - carry;
            while (t <= len)
            {
                res.Add(new PointF(a.X + (float)((b.X - a.X) * t / len), a.Y + (float)((b.Y - a.Y) * t / len)));
                t += step;
            }
            carry = len - (t - step);
        }
        if (Dist(res[res.Count - 1], src[src.Count - 1]) > step * 0.3) res.Add(src[src.Count - 1]);
        return res;
    }

    private static void Smooth(List<PointF> pts, bool closed, int passes)
    {
        int n = pts.Count;
        if (n < 3) return;
        for (int k = 0; k < passes; k++)
        {
            var copy = pts.ToArray();
            for (int i = 0; i < n; i++)
            {
                if (!closed && (i == 0 || i == n - 1)) continue;
                var p = copy[(i - 1 + n) % n]; var q = copy[(i + 1) % n];
                pts[i] = new PointF(copy[i].X * 0.5f + (p.X + q.X) * 0.25f, copy[i].Y * 0.5f + (p.Y + q.Y) * 0.25f);
            }
        }
    }

    private static (double x, double y) Tangent(List<PointF> pts, int i, bool closed)
    {
        int n = pts.Count;
        PointF a = closed ? pts[(i - 1 + n) % n] : pts[Math.Max(0, i - 1)];
        PointF b = closed ? pts[(i + 1) % n] : pts[Math.Min(n - 1, i + 1)];
        double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
        return len < 1e-9 ? (1, 0) : (dx / len, dy / len);
    }

    private static (double x, double y) Normal(List<PointF> pts, int i, bool closed)
    {
        var (tx, ty) = Tangent(pts, i, closed);
        return (-ty, tx);
    }

    private static List<PointF> Offset(List<PointF> pts, double d, bool closed)
    {
        var res = new List<PointF>(pts.Count);
        for (int i = 0; i < pts.Count; i++)
        {
            var (nx, ny) = Normal(pts, i, closed);
            res.Add(new PointF(pts[i].X + (float)(nx * d), pts[i].Y + (float)(ny * d)));
        }
        return res;
    }

    // ───────────────────── 길 위 위치 ─────────────────────

    private double Wrap(double s) => _fLen <= 0 ? 0 : ((s % _fLen) + _fLen) % _fLen;

    private PointF PointAt(double s)
    {
        s = Wrap(s);
        var p = _fPath!;
        int lo = 0, hi = p.Count - 1;
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (_fCum[mid] <= s) lo = mid; else hi = mid - 1; }
        double seg = _fCum[lo + 1] - _fCum[lo];
        double t = seg < 1e-9 ? 0 : (s - _fCum[lo]) / seg;
        PointF a = p[lo], b = p[(lo + 1) % p.Count];
        return new PointF(a.X + (float)((b.X - a.X) * t), a.Y + (float)((b.Y - a.Y) * t));
    }

    private (double x, double y) TangentAt(double s)
    {
        var a = PointAt(s - 4);
        var b = PointAt(s + 4);
        double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
        return len < 1e-9 ? (1, 0) : (dx / len, dy / len);
    }

    /// <summary>[from, to] 길이 구간에서 (x, y)에 가장 가까운 길 위 위치</summary>
    private double ClosestOnPath(double x, double y, double from, double to)
    {
        var p = _fPath!;
        int n = p.Count;
        double best = double.MaxValue, bestS = Wrap(from);
        bool whole = to - from >= _fLen;
        for (int i = 0; i < n; i++)
        {
            double segStart = _fCum[i];
            if (!whole)
            {
                // 구간 안의 선분만 (고리를 넘어가는 경우 포함)
                double rel = Wrap(segStart - from);
                double relEnd = rel + (_fCum[i + 1] - _fCum[i]);
                if (rel > to - from && relEnd < _fLen) continue;
            }
            PointF a = p[i], b = p[(i + 1) % n];
            double dd = Track.SegmentDistance(x, y, a, b, out double px, out double py);
            if (dd < best)
            {
                best = dd;
                bestS = segStart + Dist(a, new PointF((float)px, (float)py));
            }
        }
        return Wrap(bestS);
    }

    private static double Dist(PointF a, PointF b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
