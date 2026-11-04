using System;

namespace PsExporter.Services;

/// <summary>
/// 单一收纳程度（0=展开，1=收起），与播放方向解耦。
/// 重定向时继承当前位置与速度，不重新从端点播放；只在 UI 线程访问。
/// </summary>
internal sealed class DockAnimationMotion
{
    internal const int HideDurationMs = 320;
    internal const int RevealDurationMs = 280;
    internal readonly record struct Frame(double Collapse, double Velocity, bool Complete);

    private double _start;
    private double _velocity;
    private double _target;
    private double _brakeStart;
    private double _brakeVelocity;
    private double _brakeDurationMs;
    private double _travelDurationMs;
    internal bool IsHiding => _target == 1;
    internal double DurationMs => _brakeDurationMs + _travelDurationMs;

    internal void Start(bool hiding, int durationMs)
        // 首帧就有位移；之前零初速叠加漏斗错位，会让前 1/4 段像停在原地。
        => SetTrajectory(hiding ? 0 : 1,
            (hiding ? 0.9 : -1.65) / Math.Max(1, durationMs), hiding, durationMs);

    internal static int PointerDuration(bool hiding, double distanceDip)
        => (hiding ? 180 : 165) + (int)Math.Round(Math.Clamp(distanceDip * 0.04, 0, 40));

    internal void Retarget(bool hiding, double elapsedMs, int? durationMs = null)
    {
        var current = Sample(elapsedMs);
        SetTrajectory(current.Collapse, current.Velocity, hiding,
            durationMs ?? (hiding ? HideDurationMs : RevealDurationMs));
    }

    private void SetTrajectory(double start, double velocity, bool hiding, int durationMs)
    {
        _start = Math.Clamp(start, 0, 1);
        _velocity = velocity;
        _target = hiding ? 1 : 0;
        _brakeDurationMs = 0;
        if (velocity != 0 && Math.Sign(velocity) != Math.Sign(_target - _start))
        {
            _brakeStart = _start;
            _brakeVelocity = velocity;
            var room = velocity > 0 ? 1 - _start : _start;
            // 先短促制动，再走新的方向；近端点反向也不会把整段展开挤成几十毫秒。
            _brakeDurationMs = Math.Min(45, 2.4 * room / Math.Abs(velocity));
            _start = Math.Clamp(_start + velocity * _brakeDurationMs / 3, 0, 1);
            _velocity = 0;
        }
        // 半途折返只走剩余距离，避免每次都重新等待一次完整时长。
        _travelDurationMs = Math.Max(1, durationMs) * Math.Clamp(Math.Sqrt(Math.Abs(_target - _start)), 0.32, 1);
        if (_velocity == 0 && Math.Abs(_target - _start) < 0.000001)
            _travelDurationMs = 1;
        if (_velocity != 0)
        {
            var room = _velocity > 0 ? 1 - _start : _start;
            var middle = _start + (_target - _start) * (hiding ? 0.4 : 0.5);
            var middleRoom = _velocity > 0 ? 1 - middle : middle;
            // 按六次 Bernstein 控制点约束制动距离，保证整条曲线留在 0..1。
            var safeDuration = Math.Min(3 * room, 5 * middleRoom) / Math.Abs(_velocity);
            _travelDurationMs = Math.Max(0.1, Math.Min(_travelDurationMs, safeDuration));
        }
    }

    internal Frame Sample(double elapsedMs)
    {
        if (elapsedMs < _brakeDurationMs)
        {
            var brake = Math.Clamp(elapsedMs / _brakeDurationMs, 0, 1);
            var brakePosition = _brakeStart + _brakeVelocity * _brakeDurationMs
                * (brake - brake * brake + brake * brake * brake / 3);
            return new Frame(Math.Clamp(brakePosition, 0, 1), _brakeVelocity * (1 - brake) * (1 - brake), false);
        }
        var t = Math.Clamp((elapsedMs - _brakeDurationMs) / _travelDurationMs, 0, 1);
        if (t >= 1) return new Frame(_target, 0, true);
        var t2 = t * t;
        var t3 = t2 * t;
        var t4 = t3 * t;
        var t5 = t4 * t;
        var tail = 1 - t;
        // 方向节奏主要由初速区分，末端位置、速度和加速度仍平滑归位。
        var bias = IsHiding ? -2d : 0d;
        var progress = 10 * t3 - 15 * t4 + 6 * t5 + bias * t3 * tail * tail * tail;
        var slope = t2 * tail * tail * (30 + 3 * bias * (1 - 2 * t));
        // 此基函数在起点导数为 1，终点位置/速度/加速度均为 0。
        var momentum = t - 6 * t3 + 8 * t4 - 3 * t5;
        var momentumSlope = 1 - 18 * t2 + 32 * t3 - 15 * t4;
        var position = _start + (_target - _start) * progress + _velocity * _travelDurationMs * momentum;
        var velocity = (_target - _start) * slope / _travelDurationMs + _velocity * momentumSlope;
        var bounded = Math.Clamp(position, 0, 1);
        return new Frame(bounded, bounded == position ? velocity : 0, false);
    }

    internal static double IconVisibility(double collapse)
    {
        var t = Math.Clamp((collapse - 0.64) / 0.36, 0, 1);
        return t * t * (3 - 2 * t);
    }

    internal static double MeshVisibility(double collapse)
        => MeshVisibility(collapse, 0.84);

    internal static double PointerMeshVisibility(double collapse)
        => MeshVisibility(collapse, 0.72);

    private static double MeshVisibility(double collapse, double fadeStart)
    {
        var t = Math.Clamp((collapse - fadeStart) / (1 - fadeStart), 0, 1);
        return 1 - t * t * (3 - 2 * t);
    }
}
