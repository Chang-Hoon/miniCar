namespace miniCar;

/// <summary>
/// 탑뷰 게임식 조작의 16방향 선택기 (22.5° 단위).
///
///  - 방향키 하나            → 그 방향 (0°, 90°, 180°, 270°)
///  - 한 방향을 누른 채 옆 방향키를 톡(TapSeconds 미만) → 그쪽으로 22.5° 꺾고 유지
///    (한 번 더 톡 → 45°, 반대쪽 톡 → 되돌림)
///  - 한 방향을 누른 채 옆 방향키를 계속 누름 → 대각선 45°, 떼면 누르기 전 방향으로 복귀
///  - 두 키를 동시에 누름   → 대각선 45°
///  - 대각선에서 처음 키를 뗌 → 남은 키 방향
///
/// 각도는 화면 기준 (0° = →, 90° = ↓, 시계 방향 증가).
/// </summary>
internal sealed class Direction16
{
    public const double TapSeconds = 0.2;

    // 기본 방향 인덱스: 0 = →, 1 = ↓, 2 = ←, 3 = ↑ (각도 = index × 90°)
    private int _base = -1;
    private int _nudge;          // 22.5° 단계 수, + = 시계 방향, -2..2
    private int _perp = -1;      // 누르고 있는 옆 방향키
    private double _perpHeld;
    private int _nudgeBeforePerp;

    /// <summary>현재 목표 각도. 방향키를 아무것도 누르지 않았으면 null.</summary>
    public double? Update(ManualInput k, double dt)
    {
        var held = new List<int>(2);
        int h = (k.Right ? 1 : 0) - (k.Left ? 1 : 0);
        int v = (k.Down ? 1 : 0) - (k.Up ? 1 : 0);
        if (h == 1) held.Add(0);
        if (h == -1) held.Add(2);
        if (v == 1) held.Add(1);
        if (v == -1) held.Add(3);

        if (held.Count == 0)
        {
            Reset();
            return null;
        }

        if (held.Count == 1)
        {
            int c = held[0];
            if (_base == c)
            {
                if (_perp != -1)
                {
                    // 옆 방향키를 뗌: 톡 쳤으면 꺾인 방향 유지, 오래 눌렀으면 원래대로
                    if (_perpHeld >= TapSeconds) _nudge = _nudgeBeforePerp;
                    _perp = -1;
                }
            }
            else
            {
                // 새로 누름, 대각선에서 기본키를 뗌, 반대 방향으로 바꿈
                _base = c;
                _nudge = 0;
                _perp = -1;
            }
        }
        else
        {
            int a = held[0], b = held[1];
            if (_base == a || _base == b)
            {
                int p = _base == a ? b : a;
                int sign = StepSign(_base, p);
                if (_perp != p)
                {
                    // 옆 방향키를 새로 누름 → 바로 한 단계 꺾음
                    _perp = p;
                    _perpHeld = 0;
                    _nudgeBeforePerp = _nudge;
                    _nudge = MathX.Clamp(_nudge + sign, -2, 2);
                }
                else
                {
                    _perpHeld += dt;
                    if (_perpHeld >= TapSeconds) _nudge = 2 * sign; // 계속 누르면 대각선
                }
            }
            else
            {
                // 두 키를 동시에 누름 → 대각선
                _base = a;
                _perp = b;
                _perpHeld = TapSeconds;
                _nudgeBeforePerp = 0;
                _nudge = 2 * StepSign(a, b);
            }
        }

        double angle = (_base * 90 + _nudge * 22.5) % 360;
        return angle < 0 ? angle + 360 : angle;
    }

    public void Reset()
    {
        _base = -1;
        _nudge = 0;
        _perp = -1;
        _perpHeld = 0;
    }

    /// <summary>기본 방향에서 옆 방향 쪽이 시계 방향이면 +1, 반시계면 -1</summary>
    private static int StepSign(int baseIdx, int perpIdx)
    {
        int d = ((perpIdx - baseIdx) % 4 + 4) % 4; // 1 = 시계 90°, 3 = 반시계 90°
        return d == 1 ? 1 : -1;
    }
}
