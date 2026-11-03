using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using PulseIsland.Common;
using PulseIsland.Services;
using PulseIsland.ViewModels;

namespace PulseIsland
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly ISensorService _sensorService;
        private readonly SettingsService _settingsService;
        private readonly FullscreenDetectorService _fullscreenDetector;
        private readonly HotkeyService _hotkeyService;

        private readonly DispatcherTimer _collapseDebounceTimer;
        private Storyboard? _throttleStoryboard;

        private bool _isLoaded;
        private bool _isHovering;
        private bool _suppressHover;

        // Dragging state
        private bool _isMouseDown;
        private bool _isDragging;
        private Point _mouseDownScreenPoint;
        private double _initialWindowLeft;
        private double _initialWindowTop;

        public MainWindow()
        {
            InitializeComponent();

            _settingsService = new SettingsService();
            _sensorService = new HardwareMonitorService();
            _fullscreenDetector = new FullscreenDetectorService();
            _hotkeyService = new HotkeyService();

            _viewModel = new MainViewModel(_sensorService, _settingsService, _fullscreenDetector);
            DataContext = _viewModel;

            _collapseDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(280)
            };
            _collapseDebounceTimer.Tick += OnCollapseDebounceTick;

            _viewModel.ClickThroughChanged += OnClickThroughChanged;
            _viewModel.PositionModeChanged += OnPositionModeChanged;
            _viewModel.RequestResetCenter += ResetToCenter;
            _viewModel.RequestClose += () => Close();
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            Loaded += OnWindowLoaded;
            Closing += OnWindowClosing;

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;

            // Remove from Alt+Tab
            WindowHelper.SetToolWindow(this);

            // Register global hotkeys:
            // Ctrl+Alt+I -> Click-through toggle
            // Ctrl+Alt+P -> Instant hide/show toggle
            var helper = new WindowInteropHelper(this);
            _hotkeyService.Register(helper.Handle);
            _hotkeyService.ClickThroughHotkeyPressed += () => _viewModel.ToggleClickThroughCommand.Execute(null);
            _hotkeyService.VisibilityHotkeyPressed += ToggleWindowVisibility;

            // Hook WndProc for precise OS-level hit-testing (HTTRANSPARENT outside pill/card)
            var hwndSource = HwndSource.FromHwnd(helper.Handle);
            hwndSource?.AddHook(WndProc);

            // Fullscreen detector
            _fullscreenDetector.SetOwnWindow(helper.Handle);

            // Initial squircle geometry setup
            UpdateSquircleShape(300, 44);

            // Restore saved position or center
            RestoreOrCenterPosition();

            if (_viewModel.ClickThroughMode)
            {
                WindowHelper.SetClickThrough(this, true);
            }

            _sensorService.Start();
            _throttleStoryboard = TryFindResource("ThrottleBreathStoryboard") as Storyboard;
            CpuLoadArcPath.Data = ProgressArcHelper.BuildArcGeometry(13, 11, Math.Max(0.01, _viewModel.CpuLoadRingPercent));
        }

        private void ToggleWindowVisibility()
        {
            if (Visibility == Visibility.Visible)
            {
                Visibility = Visibility.Collapsed;
                _sensorService.Pause();
            }
            else
            {
                Visibility = Visibility.Visible;
                _sensorService.Resume();
            }
        }

        #region DPI-Correct Positioning & Restoration
        private void RestoreOrCenterPosition()
        {
            var helper = new WindowInteropHelper(this);
            IntPtr handle = helper.Handle;
            if (handle == IntPtr.Zero) return;

            IntPtr hMonitor = MonitorFromWindow(handle, MONITOR_DEFAULTTOPRIMARY);
            var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFOEX)) };
            if (!GetMonitorInfo(hMonitor, ref mi)) return;

            var source = PresentationSource.FromVisual(this);
            Matrix transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

            double monLeftDips = mi.rcMonitor.Left * transform.M11;
            double monWidthDips = (mi.rcMonitor.Right - mi.rcMonitor.Left) * transform.M11;
            double monTopDips = mi.rcMonitor.Top * transform.M22;

            double windowWidth = ActualWidth > 0 ? ActualWidth : Width;
            double monCenterX = monLeftDips + (monWidthDips / 2.0);

            var s = _settingsService.CurrentSettings;
            string currentRes = $"{mi.rcMonitor.Right - mi.rcMonitor.Left}x{mi.rcMonitor.Bottom - mi.rcMonitor.Top}";

            // Check if saved position matches this monitor device and resolution
            if (!s.IsPositionLocked &&
                s.SavedOffsetX.HasValue &&
                s.SavedOffsetY.HasValue &&
                string.Equals(s.SavedMonitorDevice, mi.szDevice, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.SavedMonitorResolution, currentRes, StringComparison.OrdinalIgnoreCase))
            {
                Left = (monCenterX - (windowWidth / 2.0)) + s.SavedOffsetX.Value;
                Top = monTopDips + s.SavedOffsetY.Value;
            }
            else
            {
                // Top center on primary monitor
                Left = monLeftDips + (monWidthDips - windowWidth) / 2.0;
                bool isNotch = _viewModel.PositionMode.Equals("Notch", StringComparison.OrdinalIgnoreCase);
                Top = isNotch ? monTopDips : (monTopDips + 8.0);
            }

            UpdateDiagTelemetry(Left, Top, monCenterX);
        }

        public void ResetToCenter()
        {
            var helper = new WindowInteropHelper(this);
            IntPtr handle = helper.Handle;
            if (handle == IntPtr.Zero) return;

            IntPtr hMonitor = MonitorFromWindow(handle, MONITOR_DEFAULTTOPRIMARY);
            var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFOEX)) };
            if (!GetMonitorInfo(hMonitor, ref mi)) return;

            var source = PresentationSource.FromVisual(this);
            Matrix transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

            double monLeftDips = mi.rcMonitor.Left * transform.M11;
            double monWidthDips = (mi.rcMonitor.Right - mi.rcMonitor.Left) * transform.M11;
            double monTopDips = mi.rcMonitor.Top * transform.M22;

            double windowWidth = ActualWidth > 0 ? ActualWidth : Width;
            double monCenterX = monLeftDips + (monWidthDips / 2.0);

            Left = monLeftDips + (monWidthDips - windowWidth) / 2.0;
            bool isNotch = _viewModel.PositionMode.Equals("Notch", StringComparison.OrdinalIgnoreCase);
            Top = isNotch ? monTopDips : (monTopDips + 8.0);

            // Clear saved offsets
            _settingsService.CurrentSettings.SavedOffsetX = null;
            _settingsService.CurrentSettings.SavedOffsetY = null;
            _settingsService.SaveSettings();

            UpdateDiagTelemetry(Left, Top, monCenterX);
        }

        private void UpdateDiagTelemetry(double left, double top, double monCenterX)
        {
            if (_sensorService is HardwareMonitorService hms)
            {
                hms.DiagWindowLeft = left;
                hms.DiagWindowWidth = Width;
                hms.DiagWindowActualWidth = ActualWidth;
                hms.DiagComputedCenterX = monCenterX;
            }
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            if (_isLoaded)
            {
                RestoreOrCenterPosition();
            }
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_isLoaded)
                {
                    RestoreOrCenterPosition();
                }
            });
        }
        #endregion

        #region Draggable Island (Free in X and Y, 4px threshold, 12px snap)
        private void OnIslandMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_viewModel.ClickThroughMode) return;

            // Double click resets to top center
            if (e.ClickCount == 2)
            {
                ResetToCenter();
                return;
            }

            if (!_viewModel.IsPositionLocked)
            {
                _isMouseDown = true;
                _isDragging = false;
                _mouseDownScreenPoint = PointToScreen(e.GetPosition(this));
                _initialWindowLeft = Left;
                _initialWindowTop = Top;
            }
        }

        private void OnIslandMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isMouseDown || _viewModel.IsPositionLocked) return;

            Point currentScreenPoint = PointToScreen(e.GetPosition(this));
            Vector delta = currentScreenPoint - _mouseDownScreenPoint;

            // 4 px movement threshold before initiating drag
            if (!_isDragging && (Math.Abs(delta.X) > 4.0 || Math.Abs(delta.Y) > 4.0))
            {
                _isDragging = true;
                _suppressHover = true;
                CollapseCardImmediately();
                IslandRoot.CaptureMouse();
            }

            if (_isDragging)
            {
                var source = PresentationSource.FromVisual(this);
                Matrix transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

                double deltaXDips = delta.X * transform.M11;
                double deltaYDips = delta.Y * transform.M22;

                double newLeft = _initialWindowLeft + deltaXDips;
                double newTop = _initialWindowTop + deltaYDips;

                // Query monitor under window
                var helper = new WindowInteropHelper(this);
                IntPtr hMonitor = MonitorFromWindow(helper.Handle, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFOEX)) };
                if (GetMonitorInfo(hMonitor, ref mi))
                {
                    double monLeftDips = mi.rcMonitor.Left * transform.M11;
                    double monWidthDips = (mi.rcMonitor.Right - mi.rcMonitor.Left) * transform.M11;
                    double monTopDips = mi.rcMonitor.Top * transform.M22;
                    double monHeightDips = (mi.rcMonitor.Bottom - mi.rcMonitor.Top) * transform.M22;

                    double monCenterX = monLeftDips + (monWidthDips / 2.0);
                    double windowCenterX = newLeft + (Width / 2.0);

                    // Snap within 12 px of horizontal center
                    if (Math.Abs(windowCenterX - monCenterX) <= 12.0)
                    {
                        newLeft = monCenterX - (Width / 2.0);
                    }

                    // Snap within 12 px of top edge
                    double targetTop = _viewModel.PositionMode.Equals("Notch", StringComparison.OrdinalIgnoreCase)
                        ? monTopDips
                        : (monTopDips + 8.0);

                    if (Math.Abs(newTop - targetTop) <= 12.0)
                    {
                        newTop = targetTop;
                    }

                    // Clamp to visible monitor bounds
                    newLeft = Math.Clamp(newLeft, monLeftDips - (Width - 80), monLeftDips + monWidthDips - 80);
                    newTop = Math.Clamp(newTop, monTopDips, monTopDips + monHeightDips - 60);
                }

                Left = newLeft;
                Top = newTop;
            }
        }

        private void OnIslandMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                IslandRoot.ReleaseMouseCapture();

                // Save offset from monitor center X + Y with monitor device name
                var helper = new WindowInteropHelper(this);
                IntPtr hMonitor = MonitorFromWindow(helper.Handle, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFOEX)) };
                if (GetMonitorInfo(hMonitor, ref mi))
                {
                    var source = PresentationSource.FromVisual(this);
                    Matrix transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

                    double monLeftDips = mi.rcMonitor.Left * transform.M11;
                    double monWidthDips = (mi.rcMonitor.Right - mi.rcMonitor.Left) * transform.M11;
                    double monTopDips = mi.rcMonitor.Top * transform.M22;
                    double monCenterX = monLeftDips + (monWidthDips / 2.0);

                    double offsetX = Left - (monCenterX - (Width / 2.0));
                    double offsetY = Top - monTopDips;

                    _settingsService.CurrentSettings.SavedOffsetX = offsetX;
                    _settingsService.CurrentSettings.SavedOffsetY = offsetY;
                    _settingsService.CurrentSettings.SavedMonitorDevice = mi.szDevice;
                    _settingsService.CurrentSettings.SavedMonitorResolution = $"{mi.rcMonitor.Right - mi.rcMonitor.Left}x{mi.rcMonitor.Bottom - mi.rcMonitor.Top}";
                    _settingsService.SaveSettings();
                }
            }
            else if (_isMouseDown && !_isDragging)
            {
                // Click on the pill toggles expanded state
                if (_viewModel.IsExpanded)
                {
                    CollapseCard();
                }
                else
                {
                    ExpandCard();
                }
            }

            _isMouseDown = false;
            _isDragging = false;
            _suppressHover = false;
        }

        private void CollapseCardImmediately()
        {
            _collapseDebounceTimer.Stop();
            _isHovering = false;
            _viewModel.IsExpanded = false;

            ExpandedView.Visibility = Visibility.Collapsed;
            ExpandedView.Opacity = 0.0;

            IslandRoot.Width = 300;
            IslandRoot.Height = 44;
            UpdateSquircleShape(300, 44);

            CollapsedView.Visibility = Visibility.Visible;
            CollapsedView.Opacity = 1.0;
        }
        #endregion

        #region Squircle Geometry
        private void UpdateSquircleShape(double width, double height)
        {
            bool isNotch = _viewModel.PositionMode.Equals("Notch", StringComparison.OrdinalIgnoreCase);
            double radius = _viewModel.IsExpanded ? 28.0 : (height / 2.0);

            var geom = SquircleHelper.BuildSquircleGeometry(width, height, radius, isNotch);
            SquircleShapePath.Data = geom;
            SquircleShadowPath.Data = geom;
        }

        private void OnPositionModeChanged(string mode)
        {
            RestoreOrCenterPosition();
            UpdateSquircleShape(IslandRoot.Width, IslandRoot.Height);
        }
        #endregion

        #region Spring Motion & Micro-Interactions
        private void OnIslandMouseEnter(object sender, MouseEventArgs e)
        {
            if (_suppressHover || _isDragging) return;

            _collapseDebounceTimer.Stop();

            if (_isHovering || _viewModel.IsExpanded) return;
            _isHovering = true;
            ExpandCard();
        }

        private void ExpandCard()
        {
            _collapseDebounceTimer.Stop();
            _isHovering = true;
            _viewModel.IsExpanded = true;

            // Micro-interaction: Tiny scale anticipation to 1.03
            var anticipateAnim = new DoubleAnimation
            {
                To = 1.03,
                Duration = TimeSpan.FromMilliseconds(50),
                AutoReverse = true
            };
            IslandScale.BeginAnimation(ScaleTransform.ScaleXProperty, anticipateAnim);
            IslandScale.BeginAnimation(ScaleTransform.ScaleYProperty, anticipateAnim);

            // Damped spring easing: 350ms, damping ~0.8
            var springEase = new DampedSpringEase { DampingRatio = 0.8, Frequency = 14.0 };
            var duration = TimeSpan.FromMilliseconds(350);

            const double targetWidth = 380;
            const double targetHeight = 385;

            var widthAnim = new DoubleAnimation { To = targetWidth, Duration = duration, EasingFunction = springEase };
            var heightAnim = new DoubleAnimation { To = targetHeight, Duration = duration, EasingFunction = springEase };

            var frameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            DateTime start = DateTime.UtcNow;
            frameTimer.Tick += (s, ev) =>
            {
                UpdateSquircleShape(IslandRoot.ActualWidth, IslandRoot.ActualHeight);
                if ((DateTime.UtcNow - start).TotalMilliseconds >= 360)
                {
                    frameTimer.Stop();
                    UpdateSquircleShape(targetWidth, targetHeight);
                }
            };
            frameTimer.Start();

            IslandRoot.BeginAnimation(WidthProperty, widthAnim);
            IslandRoot.BeginAnimation(HeightProperty, heightAnim);

            // Fade out collapsed view (100ms)
            var fadeOutCollapsed = new DoubleAnimation { To = 0.0, Duration = TimeSpan.FromMilliseconds(100) };
            fadeOutCollapsed.Completed += (s, ev) => CollapsedView.Visibility = Visibility.Collapsed;
            CollapsedView.BeginAnimation(OpacityProperty, fadeOutCollapsed);

            // Staggered reveal of expanded card
            ExpandedView.Visibility = Visibility.Visible;
            var fadeInCard = new DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            ExpandedView.BeginAnimation(OpacityProperty, fadeInCard);

            var slideInCard = new DoubleAnimation
            {
                From = 10.0,
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            ExpandedViewTransform.BeginAnimation(TranslateTransform.YProperty, slideInCard);

            // Refresh CPU load arc
            CpuLoadArcPath.Data = ProgressArcHelper.BuildArcGeometry(13, 11, Math.Max(0.01, _viewModel.CpuLoadRingPercent));
        }

        private void OnIslandMouseLeave(object sender, MouseEventArgs e)
        {
            if (_isDragging) return;
            _collapseDebounceTimer.Start();
        }

        private void OnCollapseDebounceTick(object? sender, EventArgs e)
        {
            _collapseDebounceTimer.Stop();

            if (!_isHovering && !_viewModel.IsExpanded) return;
            _isHovering = false;
            CollapseCard();
        }

        private void CollapseCard()
        {
            _collapseDebounceTimer.Stop();
            _isHovering = false;
            _viewModel.IsExpanded = false;

            // Content fades out first (100 ms), then shape springs back
            var fadeOutCard = new DoubleAnimation { To = 0.0, Duration = TimeSpan.FromMilliseconds(100) };
            fadeOutCard.Completed += (s, ev) =>
            {
                ExpandedView.Visibility = Visibility.Collapsed;

                var springEase = new DampedSpringEase { DampingRatio = 0.82, Frequency = 15.0 };
                var duration = TimeSpan.FromMilliseconds(300);

                const double targetWidth = 300;
                const double targetHeight = 44;

                var widthAnim = new DoubleAnimation { To = targetWidth, Duration = duration, EasingFunction = springEase };
                var heightAnim = new DoubleAnimation { To = targetHeight, Duration = duration, EasingFunction = springEase };

                var frameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                DateTime start = DateTime.UtcNow;
                frameTimer.Tick += (s2, ev2) =>
                {
                    UpdateSquircleShape(IslandRoot.ActualWidth, IslandRoot.ActualHeight);
                    if ((DateTime.UtcNow - start).TotalMilliseconds >= 310)
                    {
                        frameTimer.Stop();
                        UpdateSquircleShape(targetWidth, targetHeight);
                    }
                };
                frameTimer.Start();

                IslandRoot.BeginAnimation(WidthProperty, widthAnim);
                IslandRoot.BeginAnimation(HeightProperty, heightAnim);

                CollapsedView.Visibility = Visibility.Visible;
                var fadeInCollapsed = new DoubleAnimation { To = 1.0, Duration = TimeSpan.FromMilliseconds(180) };
                CollapsedView.BeginAnimation(OpacityProperty, fadeInCollapsed);
            };

            ExpandedView.BeginAnimation(OpacityProperty, fadeOutCard);
        }
        #endregion

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.IsThrottling))
            {
                if (_viewModel.IsThrottling)
                {
                    _throttleStoryboard?.Begin(this, true);
                }
                else
                {
                    _throttleStoryboard?.Stop(this);
                }
            }
            else if (e.PropertyName == nameof(MainViewModel.CpuLoadRingPercent))
            {
                CpuLoadArcPath.Data = ProgressArcHelper.BuildArcGeometry(13, 11, _viewModel.CpuLoadRingPercent);
            }
        }

        private void OnClickThroughChanged(bool clickThrough)
        {
            WindowHelper.SetClickThrough(this, clickThrough);
        }

        private void OnWindowClosing(object? sender, CancelEventArgs e)
        {
            _collapseDebounceTimer.Stop();
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _hotkeyService.Dispose();
            _fullscreenDetector.Dispose();
            _sensorService.Dispose();
            _settingsService.SaveSettings();
        }

        private const int WM_NCHITTEST = 0x0084;
        private const int HTTRANSPARENT = -1;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_NCHITTEST)
            {
                if (_viewModel.ClickThroughMode)
                {
                    handled = true;
                    return new IntPtr(HTTRANSPARENT);
                }

                // Cursor position in screen coordinates
                int x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
                int y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
                Point screenPt = new Point(x, y);

                try
                {
                    Point windowPt = PointFromScreen(screenPt);

                    // Compute current bounds of the visible island shape
                    GeneralTransform transform = IslandRoot.TransformToAncestor(this);
                    Point islandPos = transform.Transform(new Point(0, 0));
                    Rect islandRect = new Rect(islandPos.X, islandPos.Y, IslandRoot.ActualWidth, IslandRoot.ActualHeight);

                    // If outside the island's visible shape, return HTTRANSPARENT so clicks pass straight through
                    if (!islandRect.Contains(windowPt))
                    {
                        handled = true;
                        return new IntPtr(HTTRANSPARENT);
                    }
                }
                catch
                {
                }
            }

            return IntPtr.Zero;
        }

        #region Win32 P/Invoke
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFOEX
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private const uint MONITOR_DEFAULTTOPRIMARY = 1;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);
        #endregion
    }
}
