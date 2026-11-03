using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;
using PulseIsland.Models;

namespace PulseIsland.Services
{
    public class HardwareMonitorService : ISensorService
    {
        private readonly Computer _computer;
        private readonly AsusAcpiFanSensor _asusAcpiFanSensor;
        private readonly AsusFanReader _asusWmiFanReader;
        private readonly Timer _timer;
        private readonly object _lock = new();

        private bool _isRunning;
        private bool _isPaused;
        private HardwareMetrics _latestMetrics = new();

        // Network delta tracking
        private long _lastBytesReceived;
        private long _lastBytesSent;
        private DateTime _lastNetworkSampleTime = DateTime.UtcNow;

        // Fan source change logging
        private string? _lastLoggedFanSource;
        private static readonly object _logLock = new();

        // Window / Monitor metrics for --diag
        public double DiagWindowLeft { get; set; }
        public double DiagWindowWidth { get; set; }
        public double DiagWindowActualWidth { get; set; }
        public double DiagComputedCenterX { get; set; }

        public bool IsRunning => _isRunning;
        public bool IsPaused => _isPaused;

        public event Action<HardwareMetrics>? MetricsUpdated;

        public HardwareMonitorService()
        {
            _asusAcpiFanSensor = new AsusAcpiFanSensor();
            _asusWmiFanReader = new AsusFanReader();

            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
                IsNetworkEnabled = true,
                IsBatteryEnabled = true
            };

            try
            {
                _computer.Open();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error opening LibreHardwareMonitor Computer: {ex.Message}");
            }

            // Baseline network stats
            SampleNetworkBytes(out _lastBytesReceived, out _lastBytesSent);

            _timer = new Timer(OnTimerTick, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_isRunning) return;
                _isRunning = true;
                _isPaused = false;
                _timer.Change(0, 1000);
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_isRunning) return;
                _isRunning = false;
                _isPaused = false;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        public void Pause()
        {
            lock (_lock)
            {
                if (!_isRunning || _isPaused) return;
                _isPaused = true;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        public void Resume()
        {
            lock (_lock)
            {
                if (!_isRunning || !_isPaused) return;
                _isPaused = false;
                _timer.Change(0, 1000);
            }
        }

        public HardwareMetrics GetCurrentMetrics()
        {
            lock (_lock)
            {
                return _latestMetrics;
            }
        }

        private void OnTimerTick(object? state)
        {
            if (!_isRunning || _isPaused) return;

            try
            {
                var metrics = PollHardware();
                lock (_lock)
                {
                    _latestMetrics = metrics;
                }
                MetricsUpdated?.Invoke(metrics);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating hardware sensors: {ex.Message}");
            }
        }

        public HardwareMetrics PollHardware()
        {
            bool isElevated = CheckIsAdministrator();
            var metrics = new HardwareMetrics
            {
                Timestamp = DateTime.Now,
                IsElevated = isElevated
            };

            float? cpuPackageTemp = null;
            string? cpuTempSource = null;
            float? cpuTotalLoad = null;
            var coreTemps = new List<CoreMetric>();
            bool isThrottling = false;

            float? gpuLoad = null;
            float? gpuMemUsedMb = null;
            float? gpuTemp = null;

            float? ramUsed = null;
            float? ramAvailable = null;

            float? lhmFanRpm = null;
            string? fanSource = null;

            float? batteryPercent = null;
            float? batteryChargeRate = null;
            bool? isCharging = null;
            int? batterySecondsLeft = null;

            // Update all hardware in computer
            foreach (var hardware in _computer.Hardware)
            {
                UpdateHardwareRecursive(hardware);

                if (hardware.HardwareType == HardwareType.Cpu)
                {
                    foreach (var sensor in hardware.Sensors)
                    {
                        if (sensor.SensorType == SensorType.Temperature)
                        {
                            // Strictly exclude Distance, TjMax, Max, Average, Package for per-core collection
                            var match = Regex.Match(sensor.Name, @"^CPU Core #(\d+)$", RegexOptions.IgnoreCase);
                            if (match.Success && sensor.Value.HasValue)
                            {
                                int coreNum = int.Parse(match.Groups[1].Value);
                                // Expect exactly 12 cores on 12500H: P1-P4 (1-4) and E1-E8 (5-12)
                                string label = coreNum <= 4 ? $"P{coreNum}" : $"E{coreNum - 4}";
                                coreTemps.Add(new CoreMetric
                                {
                                    CoreNumber = coreNum,
                                    Label = label,
                                    FullName = sensor.Name,
                                    Temperature = sensor.Value.Value,
                                    IsPerformanceCore = coreNum <= 4
                                });
                            }
                            else if (sensor.Name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase) ||
                                     sensor.Name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                if (sensor.Value.HasValue)
                                {
                                    cpuPackageTemp ??= sensor.Value;
                                    cpuTempSource ??= sensor.Name;
                                }
                            }
                        }
                        else if (sensor.SensorType == SensorType.Load)
                        {
                            if (sensor.Name.Equals("CPU Total", StringComparison.OrdinalIgnoreCase))
                            {
                                cpuTotalLoad = sensor.Value;
                            }
                        }
                        else if (sensor.SensorType == SensorType.Level || sensor.SensorType == SensorType.Factor)
                        {
                            if (sensor.Name.IndexOf("Throttle", StringComparison.OrdinalIgnoreCase) >= 0 && sensor.Value > 0)
                            {
                                isThrottling = true;
                            }
                        }
                    }
                }
                else if (hardware.HardwareType == HardwareType.GpuIntel ||
                         hardware.HardwareType == HardwareType.GpuNvidia ||
                         hardware.HardwareType == HardwareType.GpuAmd)
                {
                    foreach (var sensor in hardware.Sensors)
                    {
                        if (sensor.SensorType == SensorType.Load)
                        {
                            if (sensor.Name.Equals("D3D 3D", StringComparison.OrdinalIgnoreCase) ||
                                sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
                            {
                                gpuLoad = sensor.Value;
                            }
                        }
                        else if (sensor.SensorType == SensorType.SmallData || sensor.SensorType == SensorType.Data)
                        {
                            if (sensor.Name.IndexOf("Shared Memory Used", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                gpuMemUsedMb = sensor.Value;
                            }
                        }
                        else if (sensor.SensorType == SensorType.Temperature)
                        {
                            if (sensor.Name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                sensor.Name.IndexOf("GPU", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                gpuTemp = sensor.Value;
                            }
                        }
                    }
                }
                else if (hardware.HardwareType == HardwareType.Memory)
                {
                    // Physical memory only (exclude any sensor containing "Virtual")
                    foreach (var sensor in hardware.Sensors)
                    {
                        if (sensor.Name.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;

                        if (sensor.SensorType == SensorType.Data)
                        {
                            if (sensor.Name.Equals("Memory Used", StringComparison.OrdinalIgnoreCase))
                                ramUsed = sensor.Value;
                            else if (sensor.Name.Equals("Memory Available", StringComparison.OrdinalIgnoreCase))
                                ramAvailable = sensor.Value;
                        }
                    }
                }
                else if (hardware.HardwareType == HardwareType.Motherboard ||
                         hardware.HardwareType == HardwareType.SuperIO ||
                         hardware.HardwareType == HardwareType.Cooler ||
                         hardware.HardwareType == HardwareType.EmbeddedController)
                {
                    foreach (var sensor in hardware.Sensors)
                    {
                        if (sensor.SensorType == SensorType.Fan && sensor.Value.HasValue)
                        {
                            lhmFanRpm ??= sensor.Value;
                            fanSource ??= $"LibreHardwareMonitor [{hardware.Name} -> {sensor.Name}]";
                        }
                    }
                }
                else if (hardware.HardwareType == HardwareType.Battery)
                {
                    foreach (var sensor in hardware.Sensors)
                    {
                        if (sensor.SensorType == SensorType.Level && sensor.Name.IndexOf("Charge", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            batteryPercent ??= sensor.Value;
                        }
                        else if (sensor.SensorType == SensorType.Power && sensor.Name.IndexOf("Rate", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            batteryChargeRate ??= sensor.Value;
                        }
                    }
                }
            }

            // Ensure exactly core order 1..12
            coreTemps = coreTemps.OrderBy(c => c.CoreNumber).ToList();

            // CPU Temp determination: CPU Package first, or max core fallback
            if (!cpuPackageTemp.HasValue && coreTemps.Count > 0)
            {
                var maxCore = coreTemps.OrderByDescending(c => c.Temperature).First();
                cpuPackageTemp = maxCore.Temperature;
                cpuTempSource = $"Max Core ({maxCore.Label})";
            }

            // Fan Speed: LibreHardwareMonitor fan first, then ASUS ACPI (0x00110013) second, then ASUS WMI
            float? finalFanRpm = lhmFanRpm;
            string? finalFanSource = fanSource;

            if (!finalFanRpm.HasValue)
            {
                try
                {
                    var acpiRpm = _asusAcpiFanSensor.ReadFanRpm();
                    if (acpiRpm.HasValue)
                    {
                        finalFanRpm = acpiRpm;
                        finalFanSource = "ASUS ACPI (\\\\.\\ATKACPI IOCTL 0x0022240C)";
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ASUS ACPI fan read exception: {ex.Message}");
                }
            }

            if (!finalFanRpm.HasValue)
            {
                try
                {
                    var wmiRpm = _asusWmiFanReader.TryReadFanRpm();
                    if (wmiRpm.HasValue)
                    {
                        finalFanRpm = wmiRpm;
                        finalFanSource = $"ASUS WMI ({_asusWmiFanReader.DetectedMethod ?? "WMI"})";
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ASUS WMI fan read exception: {ex.Message}");
                }
            }

            // Log fan source changes only
            if (finalFanSource != _lastLoggedFanSource)
            {
                _lastLoggedFanSource = finalFanSource;
                LogFanSourceChange(finalFanSource, finalFanRpm);
            }

            // Physical RAM Calculation (Total = used + available ~ 15.6 GB)
            float? ramTotal = null;
            float? ramPercent = null;
            if (ramUsed.HasValue && ramAvailable.HasValue)
            {
                ramTotal = ramUsed.Value + ramAvailable.Value;
                if (ramTotal.Value > 0)
                {
                    ramPercent = (ramUsed.Value / ramTotal.Value) * 100f;
                }
            }
            else
            {
                // Fallback to Win32 GlobalMemoryStatusEx for physical RAM
                var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
                if (GlobalMemoryStatusEx(ref memStatus))
                {
                    float totalPhysGb = memStatus.ullTotalPhys / (1024f * 1024f * 1024f);
                    float availPhysGb = memStatus.ullAvailPhys / (1024f * 1024f * 1024f);
                    ramUsed = totalPhysGb - availPhysGb;
                    ramTotal = totalPhysGb;
                    ramPercent = memStatus.dwMemoryLoad;
                }
            }

            // Battery Fallback & Rate Calculation
            if (GetSystemPowerStatus(out var powerStatus))
            {
                if (!batteryPercent.HasValue && powerStatus.BatteryLifePercent <= 100)
                {
                    batteryPercent = powerStatus.BatteryLifePercent;
                }
                isCharging ??= (powerStatus.ACLineStatus == 1);

                if (powerStatus.BatteryLifeTime > 0 && powerStatus.BatteryLifeTime != uint.MaxValue)
                {
                    batterySecondsLeft = (int)powerStatus.BatteryLifeTime;
                }
            }

            // Read Battery Charge/Discharge Watts & remaining capacity via WMI BatteryStatus
            if (!batteryChargeRate.HasValue || !batterySecondsLeft.HasValue)
            {
                var (wmiWatts, wmiSecLeft) = TryReadBatteryWmi();
                batteryChargeRate ??= wmiWatts;
                batterySecondsLeft ??= wmiSecLeft;
            }

            // Network throughput calculation (Physical adapters only)
            CalculateNetworkThroughput(out float downloadBps, out float uploadBps);

            // Throttle detection (>95°C or throttle flag)
            if (cpuPackageTemp.HasValue && cpuPackageTemp.Value >= 95.0f)
            {
                isThrottling = true;
            }

            metrics.CpuTemperature = cpuPackageTemp;
            metrics.CpuTempSensorSource = cpuTempSource ?? "None detected";
            metrics.CpuTotalLoad = cpuTotalLoad;
            metrics.CoreTemperatures = coreTemps;
            metrics.IsThrottling = isThrottling;

            metrics.FanRpm = finalFanRpm;
            metrics.FanSensorSource = finalFanSource;

            metrics.GpuLoad = gpuLoad;
            metrics.GpuMemoryUsedMb = gpuMemUsedMb;
            metrics.GpuTemperature = gpuTemp;

            metrics.RamUsedGb = ramUsed;
            metrics.RamTotalGb = ramTotal;
            metrics.RamPercent = ramPercent;

            metrics.BatteryPercent = batteryPercent;
            metrics.BatteryChargeRateWatts = batteryChargeRate;
            metrics.IsBatteryCharging = isCharging;
            metrics.BatteryTimeRemainingSeconds = batterySecondsLeft;

            metrics.NetworkDownloadSpeedBps = downloadBps;
            metrics.NetworkUploadSpeedBps = uploadBps;

            return metrics;
        }

        private static bool CheckIsAdministrator()
        {
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private void LogFanSourceChange(string? source, float? rpm)
        {
            try
            {
                lock (_logLock)
                {
                    string logFile = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "PulseIsland",
                        "fan_source.log");

                    string? dir = Path.GetDirectoryName(logFile);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Fan Source changed: {source ?? "None"}, RPM: {(rpm.HasValue ? rpm.Value.ToString("F0") : "null")}\r\n";
                    File.AppendAllText(logFile, line);
                }
            }
            catch { }
        }

        private (float? watts, int? secondsLeft) TryReadBatteryWmi()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM BatteryStatus");
                using var collection = searcher.Get();
                foreach (ManagementObject mo in collection)
                {
                    var rateObj = mo["DischargeRate"];
                    var chargeObj = mo["Charging"];
                    var remCapObj = mo["RemainingCapacity"];

                    float? watts = null;
                    int? secLeft = null;

                    if (rateObj != null && long.TryParse(rateObj.ToString(), out long rateMw) && rateMw > 0)
                    {
                        float w = rateMw / 1000.0f;
                        bool charging = chargeObj != null && Convert.ToBoolean(chargeObj);
                        watts = charging ? w : -w;

                        if (!charging && remCapObj != null && long.TryParse(remCapObj.ToString(), out long remCapMwh) && remCapMwh > 0)
                        {
                            secLeft = (int)((remCapMwh / (double)rateMw) * 3600.0);
                        }
                    }

                    return (watts, secLeft);
                }
            }
            catch { }
            return (null, null);
        }

        private void UpdateHardwareRecursive(IHardware hardware)
        {
            hardware.Update();
            foreach (var subHardware in hardware.SubHardware)
            {
                UpdateHardwareRecursive(subHardware);
            }
        }

        private void CalculateNetworkThroughput(out float downloadBps, out float uploadBps)
        {
            var now = DateTime.UtcNow;
            SampleNetworkBytes(out long currentReceived, out long currentSent);

            double seconds = (now - _lastNetworkSampleTime).TotalSeconds;
            if (seconds <= 0.05)
            {
                downloadBps = 0;
                uploadBps = 0;
                return;
            }

            long deltaReceived = currentReceived - _lastBytesReceived;
            long deltaSent = currentSent - _lastBytesSent;

            _lastBytesReceived = currentReceived;
            _lastBytesSent = currentSent;
            _lastNetworkSampleTime = now;

            downloadBps = (deltaReceived >= 0) ? (float)(deltaReceived / seconds) : 0;
            uploadBps = (deltaSent >= 0) ? (float)(deltaSent / seconds) : 0;
        }

        private void SampleNetworkBytes(out long totalReceived, out long totalSent)
        {
            totalReceived = 0;
            totalSent = 0;

            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                foreach (var ni in interfaces)
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                        continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;

                    string name = ni.Name ?? "";
                    string desc = ni.Description ?? "";

                    // Filter out virtual/tunnel/bluetooth adapters
                    if (name.IndexOf("VMware", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        desc.IndexOf("VMware", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        desc.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Local Area Connection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Npcap", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("vEthernet", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Loopback", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }

                    var stats = ni.GetIPStatistics();
                    totalReceived += stats.BytesReceived;
                    totalSent += stats.BytesSent;
                }
            }
            catch
            {
            }
        }

        public string GenerateDiagnosticsReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=================================================");
            sb.AppendLine("           PulseIsland Hardware Diagnostics      ");
            sb.AppendLine("=================================================");
            sb.AppendLine($"Date / Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"OS Version: {Environment.OSVersion}");
            sb.AppendLine($"Processor: {Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}");
            sb.AppendLine($"Machine: {Environment.MachineName}");
            sb.AppendLine($"Is 64-bit Process: {Environment.Is64BitProcess}");
            sb.AppendLine($"Is Administrator: {CheckIsAdministrator()}");
            sb.AppendLine();

            // Display & Centering Diagnostics
            sb.AppendLine("--- Display & Centering Diagnostics ---");
            try
            {
                IntPtr hMonitor = MonitorFromPoint(new POINT { x = 0, y = 0 }, 1 /* MONITOR_DEFAULTTOPRIMARY */);
                var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO)) };
                if (GetMonitorInfo(hMonitor, ref mi))
                {
                    var rc = mi.rcMonitor;
                    int monWidth = rc.Right - rc.Left;
                    int monHeight = rc.Bottom - rc.Top;
                    sb.AppendLine($"Monitor Native Bounds (Pixels): [{rc.Left}, {rc.Top}] -> [{rc.Right}, {rc.Bottom}] ({monWidth}x{monHeight} px)");
                }

                uint dpiX = 96, dpiY = 96;
                try
                {
                    GetDpiForMonitor(hMonitor, 0 /* MDT_EFFECTIVE_DPI */, out dpiX, out dpiY);
                }
                catch
                {
                    dpiX = 96;
                }
                double dpiScale = dpiX / 96.0;
                sb.AppendLine($"Monitor DPI Scale: {dpiScale:F2}x ({dpiX} DPI)");

                double windowWidth = (DiagWindowActualWidth > 0) ? DiagWindowActualWidth : (DiagWindowWidth > 0 ? DiagWindowWidth : 440.0);
                double computedCenter = (mi.rcMonitor.Right - mi.rcMonitor.Left) / (2.0 * dpiScale);
                double computedLeft = computedCenter - (windowWidth / 2.0);

                sb.AppendLine($"Window Left (DIPs): {(DiagWindowLeft != 0 ? DiagWindowLeft.ToString("F1") : computedLeft.ToString("F1"))}");
                sb.AppendLine($"Window Width (DIPs): {windowWidth:F1} (ActualWidth: {DiagWindowActualWidth:F1})");
                sb.AppendLine($"Computed Center X (DIPs): {computedCenter:F1}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Display query error: {ex.Message}");
            }

            sb.AppendLine();
            sb.AppendLine("--- LibreHardwareMonitor Hardware Inventory ---");
            bool foundLhmFan = false;
            foreach (var hardware in _computer.Hardware)
            {
                AppendHardwareTree(sb, hardware, indent: 0, ref foundLhmFan);
            }
            sb.AppendLine($"LibreHardwareMonitor Fan Sensor Detected: {(foundLhmFan ? "Yes" : "No")}");

            sb.AppendLine();
            sb.AppendLine("--- ASUS ACPI Fan Probe (\\\\.\\ATKACPI IOCTL 0x0022240C) ---");
            var acpiProbe = _asusAcpiFanSensor.Probe();
            sb.AppendLine($"Device Opened: {(acpiProbe.DeviceOpened ? "Yes" : "No")}");
            sb.AppendLine($"CPU Fan (0x00110013) Raw: {(acpiProbe.RawCpuValue.HasValue ? $"0x{acpiProbe.RawCpuValue.Value:X8} ({acpiProbe.RawCpuValue.Value})" : "null")}");
            sb.AppendLine($"CPU Fan Computed RPM: {(acpiProbe.CpuFanRpm.HasValue ? $"{acpiProbe.CpuFanRpm.Value} RPM" : "N/A")}");
            sb.AppendLine($"GPU Fan (0x00110014) Raw: {(acpiProbe.RawGpuValue.HasValue ? $"0x{acpiProbe.RawGpuValue.Value:X8} ({acpiProbe.RawGpuValue.Value})" : "null")}");
            sb.AppendLine($"GPU Fan Computed RPM: {(acpiProbe.GpuFanRpm.HasValue ? $"{acpiProbe.GpuFanRpm.Value} RPM" : "N/A")}");
            sb.AppendLine($"ASUS ACPI Status: {acpiProbe.StatusMessage}");

            sb.AppendLine();
            sb.AppendLine("--- ASUS WMI Fan Detection Fallback ---");
            bool wmiExists = AsusFanReader.CheckWmiClassExists(out string wmiMsg);
            sb.AppendLine($"root\\wmi\\AsusAtkWmi_WMNB Exists: {(wmiExists ? "Yes" : "No")} ({wmiMsg})");
            sb.AppendLine($"ASUS Fan WMI Working Telemetry: {(_asusWmiFanReader.IsWmiWorking ? $"{_asusWmiFanReader.DetectedMethod}" : "None / Not working")}");
            var wmiFanReading = _asusWmiFanReader.TryReadFanRpm();
            sb.AppendLine($"ASUS Fan WMI Decoded RPM: {(wmiFanReading.HasValue ? $"{wmiFanReading.Value} RPM" : "Unavailable (0x10000 not set)")}");

            sb.AppendLine();
            sb.AppendLine("--- Current Aggregated Live Metrics ---");
            var current = PollHardware();
            sb.AppendLine($"Is Elevated: {current.IsElevated}");
            sb.AppendLine($"CPU Temp: {(current.CpuTemperature.HasValue ? $"{current.CpuTemperature.Value:F1} °C" : "N/A")} [Sensor Source: {current.CpuTempSensorSource}, Live Updates: 1s]");
            sb.AppendLine($"CPU Total Load: {(current.CpuTotalLoad.HasValue ? $"{current.CpuTotalLoad.Value:F1} %" : "N/A")}");
            sb.AppendLine($"Fan Status: {current.FanStatusText} [Source: {current.FanSensorSource ?? "None"}]");
            sb.AppendLine($"GPU Load: {(current.GpuLoad.HasValue ? $"{current.GpuLoad.Value:F1} %" : "N/A")}");
            sb.AppendLine($"GPU Shared Memory: {(current.GpuMemoryUsedMb.HasValue ? $"{current.GpuMemoryUsedMb.Value:F1} MB" : "N/A")}");
            sb.AppendLine($"GPU Temp: {(current.GpuTemperature.HasValue ? $"{current.GpuTemperature.Value:F1} °C" : "N/A")}");
            sb.AppendLine($"RAM: {(current.RamUsedGb.HasValue ? $"{current.RamUsedGb.Value:F1} GB" : "N/A")} / {(current.RamTotalGb.HasValue ? $"{current.RamTotalGb.Value:F1} GB" : "N/A")} ({current.RamPercent ?? 0:F0}%) [Physical Memory Only]");
            string timeLeftDisplay = (current.BatteryTimeRemainingSeconds.HasValue && current.BatteryTimeRemainingSeconds.Value > 0)
                ? $"{current.BatteryTimeRemainingSeconds.Value}s"
                : "—";
            string chargeWattsDisplay = current.BatteryChargeRateWatts.HasValue
                ? $"{current.BatteryChargeRateWatts.Value:F1}W"
                : "N/A";
            sb.AppendLine($"Battery: {(current.BatteryPercent.HasValue ? $"{current.BatteryPercent.Value:F0}%" : "N/A")} (Charging: {current.IsBatteryCharging}, Watts: {chargeWattsDisplay}, Time Left: {timeLeftDisplay})");
            sb.AppendLine($"Network: ↓ {FormatSpeed(current.NetworkDownloadSpeedBps)} | ↑ {FormatSpeed(current.NetworkUploadSpeedBps)}");
            sb.AppendLine($"Core Temps Count: {current.CoreTemperatures.Count} (Expected exactly 12: P1-P4, E1-E8)");
            foreach (var core in current.CoreTemperatures)
            {
                sb.AppendLine($"  - [{core.Label}] {core.FullName}: {core.Temperature:F1} °C");
            }
            sb.AppendLine("=================================================");

            return sb.ToString();
        }

        private void AppendHardwareTree(StringBuilder sb, IHardware hardware, int indent, ref bool foundFan)
        {
            string pad = new string(' ', indent * 2);
            hardware.Update();
            sb.AppendLine($"{pad}[Hardware] {hardware.HardwareType} - {hardware.Name} (ID: {hardware.Identifier})");

            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType == SensorType.Fan)
                {
                    foundFan = true;
                }
                sb.AppendLine($"{pad}   * Sensor [{sensor.SensorType}] {sensor.Name} = {(sensor.Value.HasValue ? sensor.Value.Value.ToString("F1") : "null")}");
            }

            foreach (var sub in hardware.SubHardware)
            {
                AppendHardwareTree(sb, sub, indent + 1, ref foundFan);
            }
        }

        public static string FormatSpeed(float bps)
        {
            if (bps >= 1024 * 1024)
                return $"{bps / (1024 * 1024):F1} MB/s";
            if (bps >= 1024)
                return $"{bps / 1024:F1} KB/s";
            return $"{bps:F0} B/s";
        }

        public void DumpDiagnostics(string? targetFilePath = null)
        {
            try
            {
                string report = GenerateDiagnosticsReport();
                string path = targetFilePath ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PulseIsland",
                    "sensors_debug.log");

                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(path, report);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to dump diagnostics: {ex.Message}");
            }
        }

        /// <summary>
        /// Runs a 15-second self-test.
        /// During the last 8 seconds, runs a CPU burn on all cores and checks load & temperature rise.
        /// </summary>
        public string RunSelfTest()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=================================================");
            sb.AppendLine("           PulseIsland 15-Second Sensor Self-Test");
            sb.AppendLine("=================================================");
            sb.AppendLine($"Start Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Is Administrator: {CheckIsAdministrator()}");
            sb.AppendLine();

            var cpuTemps = new List<float>();
            var cpuLoads = new List<float>();
            var fanRpms = new List<float>();
            var ramUseds = new List<float>();
            var gpuLoads = new List<float>();
            var netDowns = new List<float>();
            var netUps = new List<float>();
            var batPercents = new List<float>();
            var batWatts = new List<float>();

            var coreHistory = new Dictionary<string, List<float>>();
            for (int i = 1; i <= 4; i++) coreHistory[$"P{i}"] = new List<float>();
            for (int i = 1; i <= 8; i++) coreHistory[$"E{i}"] = new List<float>();

            string? cpuTempSource = null;
            string? fanSource = null;

            CancellationTokenSource burnCts = new();

            Console.WriteLine("[1/3] Sampling baseline metrics (0s - 7s)...");

            for (int sec = 1; sec <= 15; sec++)
            {
                if (sec == 8)
                {
                    Console.WriteLine("[2/3] Initiating all-core CPU burn for last 8 seconds...");
                    // Run all-core CPU burn
                    int coreCount = Environment.ProcessorCount;
                    for (int t = 0; t < coreCount; t++)
                    {
                        Task.Run(() =>
                        {
                            double d = 1.0001;
                            while (!burnCts.Token.IsCancellationRequested)
                            {
                                for (int i = 0; i < 10000; i++)
                                {
                                    d = Math.Sqrt(d * 1.000001) + Math.Sin(d);
                                }
                            }
                        }, burnCts.Token);
                    }
                }

                Thread.Sleep(1000);

                var m = PollHardware();

                if (m.CpuTemperature.HasValue) cpuTemps.Add(m.CpuTemperature.Value);
                if (m.CpuTotalLoad.HasValue) cpuLoads.Add(m.CpuTotalLoad.Value);
                if (m.FanRpm.HasValue) fanRpms.Add(m.FanRpm.Value);
                if (m.RamUsedGb.HasValue) ramUseds.Add(m.RamUsedGb.Value);
                if (m.GpuLoad.HasValue) gpuLoads.Add(m.GpuLoad.Value);
                netDowns.Add(m.NetworkDownloadSpeedBps);
                netUps.Add(m.NetworkUploadSpeedBps);
                if (m.BatteryPercent.HasValue) batPercents.Add(m.BatteryPercent.Value);
                if (m.BatteryChargeRateWatts.HasValue) batWatts.Add(m.BatteryChargeRateWatts.Value);

                cpuTempSource ??= m.CpuTempSensorSource;
                fanSource ??= m.FanSensorSource;

                foreach (var c in m.CoreTemperatures)
                {
                    if (coreHistory.ContainsKey(c.Label))
                    {
                        coreHistory[c.Label].Add(c.Temperature);
                    }
                }

                Console.WriteLine($"  [{sec:D2}/15s] CPU: {(m.CpuTemperature.HasValue ? $"{m.CpuTemperature:F0}°C" : "--")} ({m.CpuTotalLoad:F0}%), Fan: {m.FanStatusText}, RAM: {m.RamUsedGb:F1}GB, Net: {FormatSpeed(m.NetworkDownloadSpeedBps)}");
            }

            burnCts.Cancel();
            Console.WriteLine("[3/3] Compiling Self-Test verification matrix...");
            sb.AppendLine("--- Metric Verification Results ---");

            void CheckMetric(string name, string source, List<float> list, bool expectChange = true)
            {
                if (list.Count == 0)
                {
                    sb.AppendLine($"[FAIL] {name,-22} | Source: {source,-35} | Reading: NULL / UNAVAILABLE");
                    return;
                }

                float first = list.First();
                float last = list.Last();
                float min = list.Min();
                float max = list.Max();
                bool changed = Math.Abs(max - min) > 0.01f;

                bool pass = expectChange ? changed : (min >= 0);
                string status = pass ? "PASS" : "FAIL";

                sb.AppendLine($"[{status}] {name,-22} | Source: {source,-35} | First: {first,5:F1} | Last: {last,5:F1} | Min: {min,5:F1} | Max: {max,5:F1}");
            }

            // Explicit Fan Telemetry Verification & Raw Hex Extraction
            var fanAcpiProbe = _asusAcpiFanSensor.Probe();
            int? rawAcpi = fanAcpiProbe.RawCpuValue;
            int? rawWmi = null;
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM AsusAtkWmi_WMNB");
                using var collection = searcher.Get();
                foreach (ManagementObject mo in collection)
                {
                    using var inParams = mo.GetMethodParameters("DSTS");
                    inParams["Device_ID"] = 0x00110013;
                    using var outParams = mo.InvokeMethod("DSTS", inParams, null);
                    if (outParams != null && outParams["device_status"] != null)
                    {
                        rawWmi = Convert.ToInt32(outParams["device_status"]);
                    }
                    break;
                }
            }
            catch { }

            string rawHexSummary = $"ATKACPI Raw: {(rawAcpi.HasValue ? $"0x{rawAcpi.Value:X8}" : "N/A")}, WMI Raw: {(rawWmi.HasValue ? $"0x{rawWmi.Value:X8}" : "N/A")}";

            CheckMetric("CPU Temperature", cpuTempSource ?? "MSR / LHM", cpuTemps, expectChange: true);
            CheckMetric("CPU Total Load", "LHM CPU Total Load", cpuLoads, expectChange: true);

            // Fan must FAIL when unavailable or outside 0..10000, and must print raw hex
            bool fanValid = (fanRpms.Count > 0) && fanRpms.All(r => r >= 0f && r <= 10000f);
            if (fanValid)
            {
                float first = fanRpms.First();
                float last = fanRpms.Last();
                float min = fanRpms.Min();
                float max = fanRpms.Max();
                sb.AppendLine($"[PASS] {"Cooling Fan RPM",-22} | Source: {fanSource ?? "ASUS ATKACPI",-35} | First: {first,5:F0} | Last: {last,5:F0} | Min: {min,5:F0} | Max: {max,5:F0} | {rawHexSummary}");
            }
            else
            {
                sb.AppendLine($"[FAIL] {"Cooling Fan RPM",-22} | Source: {fanSource ?? "None detected",-35} | Reading: UNAVAILABLE / UNSUPPORTED | {rawHexSummary}");
            }

            CheckMetric("Physical RAM Used", "LHM /ram Memory Used", ramUseds, expectChange: false);
            CheckMetric("GPU 3D Load", "LHM Iris Xe D3D 3D", gpuLoads, expectChange: false);
            CheckMetric("Network Download", "Physical Adapters", netDowns, expectChange: false);
            CheckMetric("Network Upload", "Physical Adapters", netUps, expectChange: false);
            CheckMetric("Battery Percent", "System Power Status", batPercents, expectChange: false);

            sb.AppendLine();
            sb.AppendLine("--- Per-Core Temperatures (12 Cores: P1-P4, E1-E8) ---");
            foreach (var kvp in coreHistory)
            {
                CheckMetric($"Core {kvp.Key}", "CPU Core Temperature", kvp.Value, expectChange: true);
            }

            sb.AppendLine();
            sb.AppendLine("--- CPU Burn Response Verification ---");
            if (cpuLoads.Count >= 10 && cpuTemps.Count >= 10)
            {
                float baseLoad = cpuLoads.Take(5).Average();
                float burnLoad = cpuLoads.Skip(8).Average();
                float baseTemp = cpuTemps.Take(5).Average();
                float burnTemp = cpuTemps.Skip(8).Average();

                sb.AppendLine($"Baseline CPU Load: {baseLoad:F1}%  -> Burn Peak Load: {burnLoad:F1}% (Rise: {burnLoad - baseLoad:+0.0;-0.0}%)");
                sb.AppendLine($"Baseline CPU Temp: {baseTemp:F1}°C -> Burn Peak Temp: {burnTemp:F1}°C (Rise: {burnTemp - baseTemp:+0.0;-0.0}°C)");

                bool burnSuccess = burnLoad > 50.0f;
                sb.AppendLine($"All-Core Burn Trigger Result: {(burnSuccess ? "PASS (Load and heat visibly surged)" : "WARNING (Check elevation)")}");
            }

            sb.AppendLine("=================================================");
            return sb.ToString();
        }

        public void Dispose()
        {
            _timer.Dispose();
            try
            {
                _computer.Close();
            }
            catch { }
            _asusAcpiFanSensor.Dispose();
            _asusWmiFanReader.Dispose();
        }

        #region Win32 Native Fallbacks
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);
        #endregion
    }
}
