using System;

namespace PulseIsland.Models
{
    public class AppSettings
    {
        // Dragging is on by default, locked = false
        public bool IsPositionLocked { get; set; } = false;
        public string PositionMode { get; set; } = "Island"; // "Island" or "Notch"

        // Position stored as offset from monitor horizontal center X + top offset Y
        public double? SavedOffsetX { get; set; }
        public double? SavedOffsetY { get; set; }
        public string? SavedMonitorDevice { get; set; }
        public string? SavedMonitorResolution { get; set; }

        public bool IsTopmost { get; set; } = true;
        public bool ClickThroughMode { get; set; } = false;
        public string Theme { get; set; } = "Glass"; // "Glass", "SolidDark", "AMOLED"
        public string AccentColorHex { get; set; } = "#30D158";
        public bool AutoHideInFullscreen { get; set; } = false;
        public bool StartWithWindows { get; set; } = false;
    }
}
