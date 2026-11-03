using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using PulseIsland.Common;
using PulseIsland.Models;
using PulseIsland.Services;

namespace PulseIsland.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private readonly ISensorService _sensorService;
        private readonly SettingsService _settingsService;
        private readonly FullscreenDetectorService _fullscreenDetector;

        private readonly List<float> _cpuTempHistory = new();
        private const int MaxHistoryPoints = 60;

        // Smooth number animators (200 ms transition)
        private readonly SmoothNumberAnimator _cpuTempAnimator = new(0, 200);
        private readonly SmoothNumberAnimator _cpuLoadAnimator = new(0, 200);
        private readonly SmoothNumberAnimator _fanRpmAnimator = new(0, 200);
        private readonly SmoothNumberAnimator _gpuLoadAnimator = new(0, 200);
        private readonly SmoothNumberAnimator _ramPercentAnimator = new(0, 200);

        private bool _isElevated = true;
        private bool _hasFanSensor = false;

        // Collapsed Pill Display
        private string _cpuTempText = "—";
        private SolidColorBrush _cpuTempBrush = new(Color.FromRgb(0x30, 0xD1, 0x58));
        private string _fanRpmText = "—";
        private bool _isThrottling;

        // Expanded Card Display
        private string _cpuTotalLoadText = "—";
        private double _cpuLoadRingPercent = 0;

        // Dedicated GPU Tile
        private string _gpuLoadText = "—";
        private string _gpuMemoryText = "—";
        private string _gpuTempText = "—";

        // Dedicated RAM Tile (Physical memory only)
        private string _ramText = "—";
        private string _ramPercentText = "—";
        private double _ramPercentValue = 0;

        // Dedicated Battery Tile
        private string _batteryText = "—";
        private string _batteryStatusText = "AC Connected";

        // Dedicated Network Tile
        private string _networkDownloadText = "↓ 0 B/s";
        private string _networkUploadText = "↑ 0 B/s";

        // Sparkline
        private PointCollection _sparklinePoints = new();
        private Geometry? _sparklineFillGeometry;
        private string _sparklineMinMaxText = "—";

        // Settings / Modes
        private bool _isExpanded;
        private bool _isTopmost = true;
        private bool _clickThroughMode;
        private bool _autoHideInFullscreen;
        private bool _startWithWindows;
        private bool _isPositionLocked = false; // Dragging is on by default
        private string _positionMode = "Island"; // "Island" or "Notch"
        private string _currentTheme = "Glass";
        private Color _accentColor = Color.FromRgb(0x30, 0xD1, 0x58);

        // iOS Pure Black #000000 at ~96% opacity fill with 0.5px 8% white inner stroke
        private Brush _islandBackground = new SolidColorBrush(Color.FromArgb(245, 0, 0, 0));
        private Brush _islandBorderBrush = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));

        public ObservableCollection<CoreDisplayItem> CoreItems { get; } = new();

        public event Action<bool>? ClickThroughChanged;
        public event Action<string>? PositionModeChanged;
        public event Action? RequestResetCenter;
        public event Action? RequestClose;

        public MainViewModel(
            ISensorService sensorService,
            SettingsService settingsService,
            FullscreenDetectorService fullscreenDetector)
        {
            _sensorService = sensorService;
            _settingsService = settingsService;
            _fullscreenDetector = fullscreenDetector;

            ApplyLoadedSettings();

            _cpuTempAnimator.ValueChanged += val =>
            {
                if (val <= 0)
                {
                    CpuTempText = "—";
                }
                else
                {
                    CpuTempText = $"{Math.Round(val)}°";
                    CpuTempBrush = new SolidColorBrush(GetInterpolatedTempColor((float)val));
                }
            };

            _cpuLoadAnimator.ValueChanged += val =>
            {
                CpuTotalLoadText = $"{Math.Round(val)}%";
                CpuLoadRingPercent = Math.Clamp(val / 100.0, 0.0, 1.0);
            };

            _fanRpmAnimator.ValueChanged += val =>
            {
                if (!_isElevated)
                {
                    FanRpmText = "Run as admin";
                }
                else if (!_hasFanSensor)
                {
                    FanRpmText = "fan --";
                }
                else if (val <= 0)
                {
                    FanRpmText = "fan off";
                }
                else
                {
                    FanRpmText = $"{Math.Round(val)} rpm";
                }
            };

            _gpuLoadAnimator.ValueChanged += val =>
            {
                GpuLoadText = $"{Math.Round(val)}%";
            };

            _ramPercentAnimator.ValueChanged += val =>
            {
                RamPercentValue = val;
                RamPercentText = $"{Math.Round(val)}%";
            };

            _sensorService.MetricsUpdated += OnMetricsUpdated;
            _fullscreenDetector.FullscreenStateChanged += OnFullscreenStateChanged;

            TogglePinCommand = new RelayCommand(() => IsTopmost = !IsTopmost);
            ToggleClickThroughCommand = new RelayCommand(() => ClickThroughMode = !ClickThroughMode);
            ToggleFullscreenHideCommand = new RelayCommand(() => AutoHideInFullscreen = !AutoHideInFullscreen);
            ToggleStartWithWindowsCommand = new RelayCommand(() => StartWithWindows = !StartWithWindows);
            TogglePositionLockCommand = new RelayCommand(() => IsPositionLocked = !IsPositionLocked);
            SwitchPositionModeCommand = new RelayCommand(param => SetPositionMode(param?.ToString() ?? "Island"));
            ResetCenterCommand = new RelayCommand(() => RequestResetCenter?.Invoke());
            ChangeThemeCommand = new RelayCommand(param => SetTheme(param?.ToString() ?? "Glass"));
            ChangeAccentColorCommand = new RelayCommand(param => SetAccentColor(param?.ToString() ?? "#30D158"));
            OpenDiagnosticsCommand = new RelayCommand(OpenDiagnosticsFile);
            ExitCommand = new RelayCommand(() => RequestClose?.Invoke());
        }

        #region Public Bindable Properties
        public bool IsElevated
        {
            get => _isElevated;
            set => SetProperty(ref _isElevated, value);
        }

        public string CpuTempText
        {
            get => _cpuTempText;
            set => SetProperty(ref _cpuTempText, value);
        }

        public SolidColorBrush CpuTempBrush
        {
            get => _cpuTempBrush;
            set => SetProperty(ref _cpuTempBrush, value);
        }

        public string FanRpmText
        {
            get => _fanRpmText;
            set => SetProperty(ref _fanRpmText, value);
        }

        public bool IsThrottling
        {
            get => _isThrottling;
            set => SetProperty(ref _isThrottling, value);
        }

        public string CpuTotalLoadText
        {
            get => _cpuTotalLoadText;
            set => SetProperty(ref _cpuTotalLoadText, value);
        }

        public double CpuLoadRingPercent
        {
            get => _cpuLoadRingPercent;
            set => SetProperty(ref _cpuLoadRingPercent, value);
        }

        public string GpuLoadText
        {
            get => _gpuLoadText;
            set => SetProperty(ref _gpuLoadText, value);
        }

        public string GpuMemoryText
        {
            get => _gpuMemoryText;
            set => SetProperty(ref _gpuMemoryText, value);
        }

        public string GpuTempText
        {
            get => _gpuTempText;
            set => SetProperty(ref _gpuTempText, value);
        }

        public string RamText
        {
            get => _ramText;
            set => SetProperty(ref _ramText, value);
        }

        public string RamPercentText
        {
            get => _ramPercentText;
            set => SetProperty(ref _ramPercentText, value);
        }

        public double RamPercentValue
        {
            get => _ramPercentValue;
            set => SetProperty(ref _ramPercentValue, value);
        }

        public string BatteryText
        {
            get => _batteryText;
            set => SetProperty(ref _batteryText, value);
        }

        public string BatteryStatusText
        {
            get => _batteryStatusText;
            set => SetProperty(ref _batteryStatusText, value);
        }

        public string NetworkDownloadText
        {
            get => _networkDownloadText;
            set => SetProperty(ref _networkDownloadText, value);
        }

        public string NetworkUploadText
        {
            get => _networkUploadText;
            set => SetProperty(ref _networkUploadText, value);
        }

        public PointCollection SparklinePoints
        {
            get => _sparklinePoints;
            set => SetProperty(ref _sparklinePoints, value);
        }

        public Geometry? SparklineFillGeometry
        {
            get => _sparklineFillGeometry;
            set => SetProperty(ref _sparklineFillGeometry, value);
        }

        public string SparklineMinMaxText
        {
            get => _sparklineMinMaxText;
            set => SetProperty(ref _sparklineMinMaxText, value);
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        public bool IsTopmost
        {
            get => _isTopmost;
            set
            {
                if (SetProperty(ref _isTopmost, value))
                {
                    _settingsService.CurrentSettings.IsTopmost = value;
                    _settingsService.SaveSettings();
                }
            }
        }

        public bool ClickThroughMode
        {
            get => _clickThroughMode;
            set
            {
                if (SetProperty(ref _clickThroughMode, value))
                {
                    _settingsService.CurrentSettings.ClickThroughMode = value;
                    _settingsService.SaveSettings();
                    ClickThroughChanged?.Invoke(value);
                }
            }
        }

        public bool IsPositionLocked
        {
            get => _isPositionLocked;
            set
            {
                if (SetProperty(ref _isPositionLocked, value))
                {
                    _settingsService.CurrentSettings.IsPositionLocked = value;
                    _settingsService.SaveSettings();
                }
            }
        }

        public string PositionMode
        {
            get => _positionMode;
            set
            {
                if (SetProperty(ref _positionMode, value))
                {
                    _settingsService.CurrentSettings.PositionMode = value;
                    _settingsService.SaveSettings();
                    PositionModeChanged?.Invoke(value);
                }
            }
        }

        public bool AutoHideInFullscreen
        {
            get => _autoHideInFullscreen;
            set
            {
                if (SetProperty(ref _autoHideInFullscreen, value))
                {
                    _settingsService.CurrentSettings.AutoHideInFullscreen = value;
                    _settingsService.SaveSettings();
                    if (value)
                        _fullscreenDetector.Start();
                    else
                        _fullscreenDetector.Stop();
                }
            }
        }

        public bool StartWithWindows
        {
            get => _startWithWindows;
            set
            {
                if (SetProperty(ref _startWithWindows, value))
                {
                    _settingsService.ApplyStartWithWindows(value);
                }
            }
        }

        public string CurrentTheme
        {
            get => _currentTheme;
            set => SetProperty(ref _currentTheme, value);
        }

        public Brush IslandBackground
        {
            get => _islandBackground;
            set => SetProperty(ref _islandBackground, value);
        }

        public Brush IslandBorderBrush
        {
            get => _islandBorderBrush;
            set => SetProperty(ref _islandBorderBrush, value);
        }

        public Color AccentColor
        {
            get => _accentColor;
            set => SetProperty(ref _accentColor, value);
        }
        #endregion

        #region Commands
        public RelayCommand TogglePinCommand { get; }
        public RelayCommand ToggleClickThroughCommand { get; }
        public RelayCommand ToggleFullscreenHideCommand { get; }
        public RelayCommand ToggleStartWithWindowsCommand { get; }
        public RelayCommand TogglePositionLockCommand { get; }
        public RelayCommand SwitchPositionModeCommand { get; }
        public RelayCommand ResetCenterCommand { get; }
        public RelayCommand ChangeThemeCommand { get; }
        public RelayCommand ChangeAccentColorCommand { get; }
        public RelayCommand OpenDiagnosticsCommand { get; }
        public RelayCommand ExitCommand { get; }
        #endregion

        private void ApplyLoadedSettings()
        {
            var s = _settingsService.CurrentSettings;
            _isTopmost = s.IsTopmost;
            _clickThroughMode = s.ClickThroughMode;
            _autoHideInFullscreen = s.AutoHideInFullscreen;
            _startWithWindows = s.StartWithWindows;
            _isPositionLocked = s.IsPositionLocked;
            _positionMode = string.IsNullOrEmpty(s.PositionMode) ? "Island" : s.PositionMode;

            SetTheme(s.Theme, save: false);
            SetAccentColor(s.AccentColorHex, save: false);

            if (_autoHideInFullscreen)
            {
                _fullscreenDetector.Start();
            }
        }

        private void OnMetricsUpdated(HardwareMetrics m)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                IsElevated = m.IsElevated;

                // CPU Temperature & Smooth Animation
                if (m.CpuTemperature.HasValue)
                {
                    _cpuTempAnimator.AnimateTo(m.CpuTemperature.Value);
                    UpdateSparkline(m.CpuTemperature.Value);
                }
                else
                {
                    _cpuTempAnimator.SetImmediately(0);
                    CpuTempText = "—";
                }

                // Throttle warning
                IsThrottling = m.IsThrottling;

                // Fan RPM
                if (!m.IsElevated)
                {
                    FanRpmText = "Run as admin";
                }
                else if (m.FanRpm.HasValue)
                {
                    _hasFanSensor = true;
                    _fanRpmAnimator.AnimateTo(m.FanRpm.Value);
                }
                else
                {
                    _hasFanSensor = false;
                    FanRpmText = "fan --";
                }

                // CPU Load
                if (m.CpuTotalLoad.HasValue)
                {
                    _cpuLoadAnimator.AnimateTo(m.CpuTotalLoad.Value);
                }
                else
                {
                    CpuTotalLoadText = "—";
                }

                // Per-Core Temps (Expected exactly 12: P1-P4, E1-E8)
                UpdateCoreMetrics(m.CoreTemperatures);

                // Dedicated GPU Tile
                if (m.GpuLoad.HasValue)
                {
                    _gpuLoadAnimator.AnimateTo(m.GpuLoad.Value);
                }
                else
                {
                    GpuLoadText = "—";
                }

                if (m.GpuMemoryUsedMb.HasValue)
                {
                    GpuMemoryText = m.GpuMemoryUsedMb.Value >= 1024
                        ? $"{m.GpuMemoryUsedMb.Value / 1024.0:F1} GB Shared"
                        : $"{m.GpuMemoryUsedMb.Value:F0} MB Shared";
                }
                else
                {
                    GpuMemoryText = "—";
                }

                GpuTempText = m.GpuTemperature.HasValue ? $"{Math.Round(m.GpuTemperature.Value)}°C" : "—";

                // Dedicated RAM Tile (Physical RAM only)
                if (m.RamUsedGb.HasValue && m.RamTotalGb.HasValue)
                {
                    RamText = $"{m.RamUsedGb.Value:F1} / {m.RamTotalGb.Value:F1} GB";
                }
                else
                {
                    RamText = "—";
                }

                if (m.RamPercent.HasValue)
                {
                    _ramPercentAnimator.AnimateTo(m.RamPercent.Value);
                }
                else
                {
                    RamPercentText = "—";
                }

                // Dedicated Battery Tile
                if (m.BatteryPercent.HasValue)
                {
                    BatteryText = $"{Math.Round(m.BatteryPercent.Value)}%";

                    string rateStr = m.BatteryChargeRateWatts.HasValue
                        ? $" ({(m.BatteryChargeRateWatts.Value >= 0 ? "+" : "")}{m.BatteryChargeRateWatts.Value:F1}W)"
                        : "";

                    if (m.IsBatteryCharging == true)
                    {
                        BatteryStatusText = $"Charging{rateStr}";
                    }
                    else
                    {
                        string timeLeftStr = "";
                        if (m.BatteryTimeRemainingSeconds.HasValue && m.BatteryTimeRemainingSeconds.Value > 0)
                        {
                            int hours = m.BatteryTimeRemainingSeconds.Value / 3600;
                            int mins = (m.BatteryTimeRemainingSeconds.Value % 3600) / 60;
                            timeLeftStr = hours > 0 ? $" · {hours}h {mins}m" : $" · {mins}m";
                        }
                        BatteryStatusText = $"On Battery{rateStr}{timeLeftStr}";
                    }
                }
                else
                {
                    BatteryText = "100%";
                    BatteryStatusText = "AC Connected";
                }

                // Dedicated Network Tile
                NetworkDownloadText = $"↓ {HardwareMonitorService.FormatSpeed(m.NetworkDownloadSpeedBps)}";
                NetworkUploadText = $"↑ {HardwareMonitorService.FormatSpeed(m.NetworkUploadSpeedBps)}";
            });
        }

        private void UpdateCoreMetrics(List<CoreMetric> cores)
        {
            if (cores.Count == 0)
            {
                if (CoreItems.Count > 0) CoreItems.Clear();
                return;
            }

            if (CoreItems.Count != cores.Count)
            {
                CoreItems.Clear();
                foreach (var c in cores)
                {
                    CoreItems.Add(new CoreDisplayItem
                    {
                        CoreName = c.Label,
                        Temperature = $"{Math.Round(c.Temperature)}°",
                        TempBrush = new SolidColorBrush(GetInterpolatedTempColor(c.Temperature))
                    });
                }
            }
            else
            {
                for (int i = 0; i < cores.Count; i++)
                {
                    CoreItems[i].CoreName = cores[i].Label;
                    CoreItems[i].Temperature = $"{Math.Round(cores[i].Temperature)}°";
                    CoreItems[i].TempBrush = new SolidColorBrush(GetInterpolatedTempColor(cores[i].Temperature));
                }
            }
        }

        public static Color GetInterpolatedTempColor(float temp)
        {
            var green = Color.FromRgb(0x30, 0xD1, 0x58);
            var yellow = Color.FromRgb(0xFF, 0xD6, 0x0A);
            var orange = Color.FromRgb(0xFF, 0x9F, 0x0A);
            var red = Color.FromRgb(0xFF, 0x45, 0x3A);

            if (temp <= 60.0f) return green;
            if (temp <= 75.0f)
            {
                float t = (temp - 60.0f) / 15.0f;
                return InterpolateColor(green, yellow, t);
            }
            if (temp <= 85.0f)
            {
                float t = (temp - 75.0f) / 10.0f;
                return InterpolateColor(yellow, orange, t);
            }

            float tOver = Math.Clamp((temp - 85.0f) / 10.0f, 0.0f, 1.0f);
            return InterpolateColor(orange, red, tOver);
        }

        private static Color InterpolateColor(Color c1, Color c2, float t)
        {
            t = Math.Clamp(t, 0.0f, 1.0f);
            byte r = (byte)(c1.R + (c2.R - c1.R) * t);
            byte g = (byte)(c1.G + (c2.G - c1.G) * t);
            byte b = (byte)(c1.B + (c2.B - c1.B) * t);
            return Color.FromRgb(r, g, b);
        }

        private void UpdateSparkline(float latestTemp)
        {
            _cpuTempHistory.Add(latestTemp);
            if (_cpuTempHistory.Count > MaxHistoryPoints)
            {
                _cpuTempHistory.RemoveAt(0);
            }

            if (_cpuTempHistory.Count < 2) return;

            const double width = 310.0;
            const double height = 36.0;

            float minTemp = Math.Min(35.0f, _cpuTempHistory.Min() - 2);
            float maxTemp = Math.Max(75.0f, _cpuTempHistory.Max() + 2);
            float tempRange = Math.Max(10.0f, maxTemp - minTemp);

            SparklineMinMaxText = $"{minTemp:F0}°C - {maxTemp:F0}°C";

            var points = new PointCollection();
            double stepX = width / (MaxHistoryPoints - 1);
            int startIndex = MaxHistoryPoints - _cpuTempHistory.Count;

            for (int i = 0; i < _cpuTempHistory.Count; i++)
            {
                double x = (startIndex + i) * stepX;
                double normalized = (_cpuTempHistory[i] - minTemp) / tempRange;
                normalized = Math.Clamp(normalized, 0.0, 1.0);
                double y = height - (normalized * (height - 4)) - 2;
                points.Add(new Point(x, y));
            }

            SparklinePoints = points;

            if (points.Count > 1)
            {
                var figure = new PathFigure { StartPoint = new Point(points[0].X, height), IsClosed = true, IsFilled = true };
                figure.Segments.Add(new LineSegment(points[0], true));
                for (int i = 1; i < points.Count; i++)
                {
                    figure.Segments.Add(new LineSegment(points[i], true));
                }
                figure.Segments.Add(new LineSegment(new Point(points[^1].X, height), true));

                var geometry = new PathGeometry();
                geometry.Figures.Add(figure);
                SparklineFillGeometry = geometry;
            }
        }

        private void OnFullscreenStateChanged(bool isFullscreen)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (isFullscreen)
                {
                    _sensorService.Pause();
                    if (Application.Current.MainWindow != null)
                    {
                        Application.Current.MainWindow.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    if (Application.Current.MainWindow != null)
                    {
                        Application.Current.MainWindow.Visibility = Visibility.Visible;
                    }
                    _sensorService.Resume();
                }
            });
        }

        #region Theme and Customization
        public void SetPositionMode(string mode)
        {
            PositionMode = mode;
        }

        public void SetTheme(string themeName, bool save = true)
        {
            CurrentTheme = themeName;
            switch (themeName.ToLowerInvariant())
            {
                case "soliddark":
                    IslandBackground = new SolidColorBrush(Color.FromRgb(22, 24, 28));
                    IslandBorderBrush = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
                    break;
                case "amoled":
                case "amoledblack":
                    CurrentTheme = "AMOLED";
                    IslandBackground = new SolidColorBrush(Color.FromRgb(0, 0, 0));
                    IslandBorderBrush = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255));
                    break;
                case "glass":
                default:
                    CurrentTheme = "Glass";
                    IslandBackground = new SolidColorBrush(Color.FromArgb(245, 0, 0, 0));
                    IslandBorderBrush = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
                    break;
            }

            if (save)
            {
                _settingsService.CurrentSettings.Theme = CurrentTheme;
                _settingsService.SaveSettings();
            }
        }

        public void SetAccentColor(string hex, bool save = true)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                AccentColor = color;
                if (save)
                {
                    _settingsService.CurrentSettings.AccentColorHex = hex;
                    _settingsService.SaveSettings();
                }
            }
            catch { }
        }

        private void OpenDiagnosticsFile()
        {
            try
            {
                _sensorService.DumpDiagnostics();
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PulseIsland",
                    "sensors_debug.log");

                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open diagnostics: {ex.Message}");
            }
        }
        #endregion
    }

    public class CoreDisplayItem : ViewModelBase
    {
        private string _coreName = string.Empty;
        private string _temperature = "—";
        private SolidColorBrush _tempBrush = new(Colors.White);

        public string CoreName
        {
            get => _coreName;
            set => SetProperty(ref _coreName, value);
        }

        public string Temperature
        {
            get => _temperature;
            set => SetProperty(ref _temperature, value);
        }

        public SolidColorBrush TempBrush
        {
            get => _tempBrush;
            set => SetProperty(ref _tempBrush, value);
        }
    }
}
