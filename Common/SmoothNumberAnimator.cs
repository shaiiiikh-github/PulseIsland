using System;
using System.Diagnostics;
using System.Windows.Media;

namespace PulseIsland.Common
{
    /// <summary>
    /// Smoothly animates numeric values over a given duration (e.g. 200ms) using CompositionTarget.Rendering.
    /// Eliminates number snapping.
    /// </summary>
    public class SmoothNumberAnimator
    {
        private readonly double _durationMs;
        private double _currentValue;
        private double _targetValue;
        private double _startValue;
        private readonly Stopwatch _stopwatch = new();
        private bool _isAnimating;

        public double Value => _currentValue;
        public event Action<double>? ValueChanged;

        public SmoothNumberAnimator(double initialValue = 0, double durationMs = 200)
        {
            _currentValue = initialValue;
            _targetValue = initialValue;
            _durationMs = durationMs;
        }

        public void AnimateTo(double target)
        {
            if (Math.Abs(_targetValue - target) < 0.001) return;

            _startValue = _currentValue;
            _targetValue = target;
            _stopwatch.Restart();

            if (!_isAnimating)
            {
                _isAnimating = true;
                CompositionTarget.Rendering += OnRendering;
            }
        }

        public void SetImmediately(double val)
        {
            _currentValue = val;
            _targetValue = val;
            _isAnimating = false;
            CompositionTarget.Rendering -= OnRendering;
            ValueChanged?.Invoke(_currentValue);
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            double elapsed = _stopwatch.Elapsed.TotalMilliseconds;
            double progress = Math.Clamp(elapsed / _durationMs, 0.0, 1.0);

            // Quadratic ease out
            double ease = 1.0 - (1.0 - progress) * (1.0 - progress);
            _currentValue = _startValue + ((_targetValue - _startValue) * ease);

            ValueChanged?.Invoke(_currentValue);

            if (progress >= 1.0)
            {
                _currentValue = _targetValue;
                _isAnimating = false;
                CompositionTarget.Rendering -= OnRendering;
                ValueChanged?.Invoke(_currentValue);
            }
        }
    }
}

