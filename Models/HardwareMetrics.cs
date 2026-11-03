using System;
using System.Collections.Generic;

namespace PulseIsland.Models
{
    public class HardwareMetrics
    {
        // Elevation state
        public bool IsElevated { get; set; } = true;

        // CPU
        public float? CpuTemperature { get; set; }
        public string? CpuTempSensorSource { get; set; }
        public float? CpuTotalLoad { get; set; }
        public List<CoreMetric> CoreTemperatures { get; set; } = new();
        public bool IsThrottling { get; set; }

        // Fan
        public float? FanRpm { get; set; }
        public string? FanSensorSource { get; set; }

        // Display rules:
        // Not elevated: "Run as admin"
        // RPM > 0: "1850 rpm"
        // RPM == 0: "fan off"
        // No sensor at all: "fan --"
        public string FanStatusText
        {
            get
            {
                if (!IsElevated) return "Run as admin";
                if (!FanRpm.HasValue) return "fan --";
                if (FanRpm.Value > 0) return $"{Math.Round(FanRpm.Value)} rpm";
                return "fan off";
            }
        }

        // GPU
        public float? GpuLoad { get; set; }
        public float? GpuMemoryUsedMb { get; set; }
        public float? GpuTemperature { get; set; }

        // Memory (Physical RAM only)
        public float? RamUsedGb { get; set; }
        public float? RamTotalGb { get; set; }
        public float? RamPercent { get; set; }

        // Battery
        public float? BatteryPercent { get; set; }
        public float? BatteryChargeRateWatts { get; set; }
        public bool? IsBatteryCharging { get; set; }
        public int? BatteryTimeRemainingSeconds { get; set; }

        // Network (Bytes/sec, physical adapters only)
        public float NetworkDownloadSpeedBps { get; set; }
        public float NetworkUploadSpeedBps { get; set; }

        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    public class CoreMetric
    {
        public int CoreNumber { get; set; }
        public string Label { get; set; } = string.Empty; // P1-P4, E1-E8
        public string FullName { get; set; } = string.Empty;
        public float Temperature { get; set; }
        public bool IsPerformanceCore { get; set; }
    }
}
