using System;
using System.Windows.Media.Animation;

namespace PulseIsland.Common
{
    /// <summary>
    /// Custom damped spring easing function:
    /// f(t) = 1 - e^(-zeta * omega * t) * (cos(omega_d * t) + (zeta / sqrt(1 - zeta^2)) * sin(omega_d * t))
    /// Default: Damping ~ 0.8, slight natural overshoot.
    /// </summary>
    public class DampedSpringEase : EasingFunctionBase
    {
        public double DampingRatio { get; set; } = 0.8;
        public double Frequency { get; set; } = 14.0;

        public DampedSpringEase()
        {
            EasingMode = EasingMode.EaseOut;
        }

        protected override System.Windows.Freezable CreateInstanceCore()
        {
            return new DampedSpringEase
            {
                DampingRatio = DampingRatio,
                Frequency = Frequency,
                EasingMode = EasingMode
            };
        }

        protected override double EaseInCore(double normalizedTime)
        {
            // For spring expand/collapse, we use EaseOut formulation directly
            return 1.0 - CalculateSpring(1.0 - normalizedTime);
        }

        public double CalculateSpring(double t)
        {
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;

            double zeta = Math.Clamp(DampingRatio, 0.01, 0.99);
            double omega = Frequency;
            double omegaD = omega * Math.Sqrt(1.0 - (zeta * zeta));

            double decay = Math.Exp(-zeta * omega * t);
            double oscillation = Math.Cos(omegaD * t) + ((zeta / Math.Sqrt(1.0 - (zeta * zeta))) * Math.Sin(omegaD * t));

            return 1.0 - (decay * oscillation);
        }
    }
}

