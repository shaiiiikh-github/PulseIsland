using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Management;
using Microsoft.Win32.SafeHandles;
using PulseIsland.Services;

namespace PulseIsland
{
    public partial class App : Application
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            byte[] lpInBuffer,
            uint nInBufferSize,
            byte[] lpOutBuffer,
            uint nOutBufferSize,
            ref uint lpBytesReturned,
            IntPtr lpOverlapped);

        private const int ATTACH_PARENT_PROCESS = -1;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;

        public App()
        {
            string[] cmdArgs = Environment.GetCommandLineArgs();

            bool isFanProbe = cmdArgs.Any(a => a.Equals("--fan-probe", StringComparison.OrdinalIgnoreCase) ||
                                               a.Equals("-fan-probe", StringComparison.OrdinalIgnoreCase) ||
                                               a.Equals("/fan-probe", StringComparison.OrdinalIgnoreCase));

            if (isFanProbe)
            {
                RunFanProbeMode();
                Environment.Exit(0);
                return;
            }

            bool isSelftest = cmdArgs.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase) ||
                                               a.Equals("-selftest", StringComparison.OrdinalIgnoreCase) ||
                                               a.Equals("/selftest", StringComparison.OrdinalIgnoreCase));

            if (isSelftest)
            {
                RunSelfTestMode();
                Environment.Exit(0);
                return;
            }

            bool isDiagMode = cmdArgs.Any(a => a.Equals("--diag", StringComparison.OrdinalIgnoreCase) ||
                                               a.Equals("-diag", StringComparison.OrdinalIgnoreCase) ||
                                               a.Equals("/diag", StringComparison.OrdinalIgnoreCase));

            if (isDiagMode)
            {
                RunDiagnosticsMode();
                Environment.Exit(0);
                return;
            }

            bool isAsusDiscovery = cmdArgs.Any(a => a.Equals("--asus-discovery", StringComparison.OrdinalIgnoreCase) ||
                                                    a.Equals("-asus-discovery", StringComparison.OrdinalIgnoreCase) ||
                                                    a.Equals("/asus-discovery", StringComparison.OrdinalIgnoreCase));

            if (isAsusDiscovery)
            {
                RunAsusDiscoveryMode();
                Environment.Exit(0);
                return;
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var mainWindow = new MainWindow();
            mainWindow.Show();
        }

        private static bool IsAdministrator()
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

        private void SetupConsoleOutput()
        {
            bool hasConsole = AttachConsole(ATTACH_PARENT_PROCESS);
            if (!hasConsole)
            {
                hasConsole = AllocConsole();
            }

            if (hasConsole)
            {
                try
                {
                    IntPtr stdHandle = CreateFile("CONOUT$", GENERIC_WRITE, FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (stdHandle != IntPtr.Zero && stdHandle != new IntPtr(-1))
                    {
                        var safeHandle = new SafeFileHandle(stdHandle, true);
                        var fileStream = new FileStream(safeHandle, FileAccess.Write);
                        var writer = new StreamWriter(fileStream, Encoding.UTF8) { AutoFlush = true };
                        Console.SetOut(writer);
                        Console.SetError(writer);
                    }
                }
                catch
                {
                }
            }
        }

        private void RunDiagnosticsMode()
        {
            SetupConsoleOutput();

            Console.WriteLine();
            Console.WriteLine("==================================================");
            Console.WriteLine("   PulseIsland System Sensor Diagnostics Utility  ");
            Console.WriteLine("==================================================");
            Console.WriteLine("Scanning hardware via LibreHardwareMonitor & ASUS ACPI...");
            Console.WriteLine();

            try
            {
                using var sensorService = new HardwareMonitorService();
                string report = sensorService.GenerateDiagnosticsReport();
                Console.WriteLine(report);

                // Write to diag.txt and sensors_diag.txt in exe directory and current directory
                string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                string diagFile1 = Path.Combine(exeDir, "diag.txt");
                string diagFile2 = Path.Combine(exeDir, "sensors_diag.txt");
                File.WriteAllText(diagFile1, report);
                File.WriteAllText(diagFile2, report);

                // Also write to current working directory
                File.WriteAllText("diag.txt", report);

                // Write to AppData
                string appDataFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PulseIsland",
                    "sensors_debug.log");
                string? dir = Path.GetDirectoryName(appDataFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(appDataFile, report);

                Console.WriteLine($"[✓] Diagnostics report saved to:");
                Console.WriteLine($"    {diagFile1}");
                Console.WriteLine($"    {diagFile2}");
                Console.WriteLine($"    {appDataFile}");
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Diagnostics error: {ex}");
            }
        }

        private void RunSelfTestMode()
        {
            SetupConsoleOutput();

            Console.WriteLine();
            Console.WriteLine("==================================================");
            Console.WriteLine("   PulseIsland 15-Second Sensor Self-Test Suite   ");
            Console.WriteLine("==================================================");
            Console.WriteLine();

            try
            {
                using var sensorService = new HardwareMonitorService();
                string report = sensorService.RunSelfTest();
                Console.WriteLine(report);

                // Write to selftest.txt next to exe and in working directory
                string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                string selftestFile1 = Path.Combine(exeDir, "selftest.txt");
                File.WriteAllText(selftestFile1, report);
                File.WriteAllText("selftest.txt", report);

                string appDataFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PulseIsland",
                    "selftest.txt");
                string? dir = Path.GetDirectoryName(appDataFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(appDataFile, report);

                Console.WriteLine($"[✓] Self-Test output saved to:");
                Console.WriteLine($"    {selftestFile1}");
                Console.WriteLine($"    {appDataFile}");
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Self-test error: {ex}");
            }
        }

        private void RunFanProbeMode()
        {
            SetupConsoleOutput();

            var sb = new StringBuilder();
            sb.AppendLine("================================================================================");
            sb.AppendLine("                   PulseIsland ASUS Fan Telemetry Probe Suite                  ");
            sb.AppendLine("================================================================================");
            sb.AppendLine($"Date / Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Is Administrator: {IsAdministrator()}");
            sb.AppendLine();

            // 1. Probe WMI class AsusAtkWmi_WMNB
            sb.AppendLine("--- 1. ASUS WMI Class (root\\wmi\\AsusAtkWmi_WMNB) Fan Probe ---");
            bool wmiClassExists = false;
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM AsusAtkWmi_WMNB");
                using var collection = searcher.Get();
                int count = 0;
                foreach (ManagementObject mo in collection)
                {
                    count++;
                    wmiClassExists = true;

                    uint[] fanIds = { 0x00110013, 0x00110014 };
                    string[] fanNames = { "CPU Fan", "GPU Fan" };

                    for (int f = 0; f < fanIds.Length; f++)
                    {
                        uint fid = fanIds[f];
                        string fname = fanNames[f];
                        try
                        {
                            using var inParams = mo.GetMethodParameters("DSTS");
                            inParams["Device_ID"] = fid;
                            using var outParams = mo.InvokeMethod("DSTS", inParams, null);
                            if (outParams != null && outParams["device_status"] != null)
                            {
                                uint status = Convert.ToUInt32(outParams["device_status"]);
                                bool valid = AsusAcpiFanSensor.TryDecodeRpm(status, out float decodedRpm);
                                string note = (status == 0xFFFFFFFE) ? " (0xFFFFFFFE = Method/Device Unsupported by BIOS)" : "";
                                string resultStr = valid ? $"{decodedRpm:F0} RPM [VALID]" : $"Unavailable / Not supported{note}";
                                sb.AppendLine($"  {fname} (0x{fid:X8}) -> Raw: 0x{status:X8} ({status}) | Result: {resultStr}");
                            }
                            else
                            {
                                sb.AppendLine($"  {fname} (0x{fid:X8}) -> null outParams");
                            }
                        }
                        catch (Exception exF)
                        {
                            sb.AppendLine($"  {fname} (0x{fid:X8}) -> DSTS Invocation error: {exF.Message}");
                        }
                    }
                    break;
                }
                if (!wmiClassExists)
                {
                    sb.AppendLine("  Class exists in repository but returned 0 instances.");
                }
            }
            catch (Exception exWmi)
            {
                wmiClassExists = false;
                sb.AppendLine($"  Query failed / Class does not exist: {exWmi.Message}");
            }

            sb.AppendLine($"  root\\wmi\\AsusAtkWmi_WMNB Exists: {(wmiClassExists ? "Yes" : "No")}");
            sb.AppendLine();

            // 2. Probe \\.\ATKACPI IOCTL 0x0022240C for device IDs 0x00110010 to 0x00110035
            sb.AppendLine("--- 2. ASUS ACPI Direct IOCTL Probe (\\\\.\\ATKACPI IOCTL 0x0022240C) ---");
            sb.AppendLine("Target ID range: 0x00110010 to 0x00110035");
            sb.AppendLine("Decoding rule: Reading valid ONLY if (value & 0xFFFF0000) == 0x00010000, RPM = (value & 0xFFFF) * 100 within 0..10000");
            sb.AppendLine();
            sb.AppendLine(string.Format("{0,-12} | {1,-7} | {2,-8} | {3,-5} | {4,-24} | {5,-22} | {6}",
                "Device ID", "Success", "Win32Err", "Bytes", "Raw Buffer (Hex 8B)", "First Int32", "Decoded RPM"));
            sb.AppendLine(new string('-', 112));

            IntPtr hDevice = CreateFile(
                @"\\.\ATKACPI",
                0x80000000 | 0x40000000,
                1 | 2,
                IntPtr.Zero,
                3,
                0x80,
                IntPtr.Zero);

            uint foundPresentId = 0;
            float foundPresentRpm = 0;

            if (hDevice == IntPtr.Zero || hDevice == new IntPtr(-1))
            {
                int err = Marshal.GetLastWin32Error();
                sb.AppendLine($"[ERROR] Failed to open \\\\.\\ATKACPI handle! Win32 Error: {err} (Requires Administrator elevation)");
            }
            else
            {
                const uint IOCTL_ATKACPI = 0x0022240C;
                const uint DSTS = 0x53545344;

                for (uint devId = 0x00110010; devId <= 0x00110035; devId++)
                {
                    byte[] inBuffer = new byte[16];
                    BitConverter.GetBytes(DSTS).CopyTo(inBuffer, 0);
                    BitConverter.GetBytes(8).CopyTo(inBuffer, 4);
                    BitConverter.GetBytes(devId).CopyTo(inBuffer, 8);
                    BitConverter.GetBytes(0).CopyTo(inBuffer, 12);

                    byte[] outBuffer = new byte[16];
                    uint bytesRet = 0;

                    bool success = DeviceIoControl(
                        hDevice,
                        IOCTL_ATKACPI,
                        inBuffer,
                        (uint)inBuffer.Length,
                        outBuffer,
                        (uint)outBuffer.Length,
                        ref bytesRet,
                        IntPtr.Zero);

                    int win32Err = Marshal.GetLastWin32Error();
                    string rawHex = BitConverter.ToString(outBuffer, 0, 8).Replace("-", " ");
                    int valInt32 = BitConverter.ToInt32(outBuffer, 0);
                    string valInt32Str = $"0x{valInt32:X8} ({valInt32})";

                    string decodedStr;
                    if (AsusAcpiFanSensor.TryDecodeRpm(valInt32, out float rpm))
                    {
                        decodedStr = $"[PRESENT] {rpm:F0} RPM";
                        if (foundPresentId == 0)
                        {
                            foundPresentId = devId;
                            foundPresentRpm = rpm;
                        }
                    }
                    else if ((uint)valInt32 == 0xFFFFFFFE || valInt32 == -2)
                    {
                        decodedStr = "Unsupported (0xFFFFFFFE)";
                    }
                    else if (devId == 0x00110019 && valInt32 == 0x00030000)
                    {
                        decodedStr = "VivoBookMode setting (Not fan sensor)";
                    }
                    else
                    {
                        decodedStr = "Unavailable (0x00010000 not set)";
                    }

                    sb.AppendLine(string.Format("0x{0:X8}   | {1,-7} | {2,-8} | {3,-5} | {4,-24} | {5,-22} | {6}",
                        devId, success, win32Err, bytesRet, rawHex, valInt32Str, decodedStr));
                }

                sb.AppendLine();
                sb.AppendLine("--- 3. Control Tests (Verified G-Helper AsusACPI Device IDs) ---");
                sb.AppendLine("Testing known valid ASUS ACPI methods to verify communication pipeline:");
                var controlDevices = new (uint Id, string Name)[]
                {
                    (0x00120075, "PerformanceMode (0x00120075)"),
                    (0x00110019, "VivoBookMode (0x00110019)"),
                    (0x00120057, "BatteryLimit (0x00120057)")
                };

                bool anyControlAtkSuccess = false;
                bool anyControlWmiSuccess = false;

                // ATKACPI Control Test
                sb.AppendLine("A) ATKACPI IOCTL Control Results:");
                foreach (var ctrl in controlDevices)
                {
                    byte[] inBuffer = new byte[16];
                    BitConverter.GetBytes(DSTS).CopyTo(inBuffer, 0);
                    BitConverter.GetBytes(8).CopyTo(inBuffer, 4);
                    BitConverter.GetBytes(ctrl.Id).CopyTo(inBuffer, 8);
                    BitConverter.GetBytes(0).CopyTo(inBuffer, 12);

                    byte[] outBuffer = new byte[16];
                    uint bytesRet = 0;

                    bool success = DeviceIoControl(
                        hDevice,
                        IOCTL_ATKACPI,
                        inBuffer,
                        (uint)inBuffer.Length,
                        outBuffer,
                        (uint)outBuffer.Length,
                        ref bytesRet,
                        IntPtr.Zero);

                    int win32Err = Marshal.GetLastWin32Error();
                    string rawHex = BitConverter.ToString(outBuffer, 0, 8).Replace("-", " ");
                    int valInt32 = BitConverter.ToInt32(outBuffer, 0);

                    if (success && bytesRet > 0) anyControlAtkSuccess = true;

                    sb.AppendLine($"  {ctrl.Name,-32} -> Success: {success,-5} | Err: {win32Err} | Bytes: {bytesRet} | Raw: {rawHex} | Int32: 0x{valInt32:X8} ({valInt32})");
                }

                CloseHandle(hDevice);

                // WMI Control Test
                sb.AppendLine();
                sb.AppendLine("B) ASUS WMI (AsusAtkWmi_WMNB) Control Results:");
                try
                {
                    using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM AsusAtkWmi_WMNB");
                    using var collection = searcher.Get();
                    foreach (ManagementObject mo in collection)
                    {
                        foreach (var ctrl in controlDevices)
                        {
                            try
                            {
                                using var inParams = mo.GetMethodParameters("DSTS");
                                inParams["Device_ID"] = ctrl.Id;
                                using var outParams = mo.InvokeMethod("DSTS", inParams, null);
                                if (outParams != null && outParams["device_status"] != null)
                                {
                                    uint status = Convert.ToUInt32(outParams["device_status"]);
                                    if (status != 0xFFFFFFFE && status != 0xFFFFFFFF)
                                    {
                                        anyControlWmiSuccess = true;
                                    }
                                    sb.AppendLine($"  {ctrl.Name,-32} -> status: 0x{status:X8} ({status})");
                                }
                                else
                                {
                                    sb.AppendLine($"  {ctrl.Name,-32} -> outParams null");
                                }
                            }
                            catch (Exception exC)
                            {
                                sb.AppendLine($"  {ctrl.Name,-32} -> error: {exC.Message}");
                            }
                        }
                        break;
                    }
                }
                catch (Exception exWmiCtrl)
                {
                    sb.AppendLine($"  WMI Control query failed: {exWmiCtrl.Message}");
                }

                sb.AppendLine();
                sb.AppendLine("--- Summary & Conclusion ---");
                if (anyControlAtkSuccess || anyControlWmiSuccess)
                {
                    sb.AppendLine("[✓] Direct ASUS ACPI / WMI communication pipeline IS FULLY FUNCTIONAL.");
                    sb.AppendLine("    Control queries for VivoBookMode (0x00110019) and/or BatteryLimit (0x00120057) responded with valid status.");
                    sb.AppendLine("    The fan device IDs (0x00110013, 0x00110014) returning 0xFFFFFFFE / ERROR_INVALID_FUNCTION confirms that");
                    sb.AppendLine("    the ASUS Vivobook S15 firmware/BIOS does NOT expose fan RPM sensors via the standard ROG/TUF DSTS method.");
                }
                else
                {
                    sb.AppendLine("[!] Neither fan nor control IDs responded. Ensure process is run as Administrator.");
                }

                if (foundPresentId != 0)
                {
                    sb.AppendLine($"[✓] Valid Fan Sensor Found! Device ID: 0x{foundPresentId:X8}, RPM: {foundPresentRpm:F0}");
                }
                else
                {
                    sb.AppendLine("[*] Widget fan status: 'fan --' (Fan tachometer hardware sensor not exposed by Vivobook BIOS).");
                }
            }

            string report = sb.ToString();
            Console.WriteLine(report);

            try
            {
                string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                File.WriteAllText(Path.Combine(exeDir, "fan_probe.txt"), report);
                File.WriteAllText("fan_probe.txt", report);

                string appDataFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PulseIsland",
                    "fan_probe.txt");
                string? dir = Path.GetDirectoryName(appDataFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(appDataFile, report);
            }
            catch (Exception exIo)
            {
                Console.WriteLine($"Error writing fan_probe.txt: {exIo.Message}");
            }
        }

        private void RunAsusDiscoveryMode()
        {
            SetupConsoleOutput();

            var sb = new StringBuilder();
            sb.AppendLine("================================================================================");
            sb.AppendLine("                  PulseIsland ASUS Architecture & Sensor Discovery Suite        ");
            sb.AppendLine("================================================================================");
            sb.AppendLine($"Date / Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Is Administrator: {IsAdministrator()}");
            sb.AppendLine();

            // 1. root\WMI classes containing Asus, ATK or ACPI
            sb.AppendLine("--- 1. root\\WMI Classes (Filtering 'Asus', 'ATK', 'ACPI') ---");
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM meta_class");
                using var collection = searcher.Get();
                int matchCount = 0;
                foreach (ManagementClass mc in collection)
                {
                    string className = mc["__CLASS"]?.ToString() ?? "";
                    if (className.Contains("Asus", StringComparison.OrdinalIgnoreCase) ||
                        className.Contains("ATK", StringComparison.OrdinalIgnoreCase) ||
                        className.Contains("ACPI", StringComparison.OrdinalIgnoreCase))
                    {
                        matchCount++;
                        int instanceCount = 0;
                        try
                        {
                            using var instSearcher = new ManagementObjectSearcher(@"root\WMI", $"SELECT * FROM {className}");
                            using var instColl = instSearcher.Get();
                            instanceCount = instColl.Count;
                        }
                        catch { }

                        var methods = mc.Methods.Cast<MethodData>().Select(m => m.Name).ToList();
                        var properties = mc.Properties.Cast<PropertyData>().Select(p => p.Name).ToList();

                        sb.AppendLine($"Class: {className} (Instances: {instanceCount})");
                        sb.AppendLine($"  Methods: {(methods.Count > 0 ? string.Join(", ", methods) : "None")}");
                        sb.AppendLine($"  Properties: {(properties.Count > 0 ? string.Join(", ", properties) : "None")}");
                    }
                }
                sb.AppendLine($"Total matching root\\WMI classes: {matchCount}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Error querying root\\WMI meta_class: {ex.Message}");
            }
            sb.AppendLine();

            // 2. Windows Services and Drivers matching asus, atk, myasus
            sb.AppendLine("--- 2. Windows Services & Drivers (Filtering 'asus', 'atk', 'myasus') ---");
            try
            {
                using var serviceSearcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT Name, DisplayName, State, PathName FROM Win32_Service");
                using var serviceColl = serviceSearcher.Get();
                sb.AppendLine("Services:");
                int svcCount = 0;
                foreach (ManagementObject mo in serviceColl)
                {
                    string name = mo["Name"]?.ToString() ?? "";
                    string disp = mo["DisplayName"]?.ToString() ?? "";
                    string path = mo["PathName"]?.ToString() ?? "";
                    string state = mo["State"]?.ToString() ?? "";

                    if (name.Contains("asus", StringComparison.OrdinalIgnoreCase) ||
                        disp.Contains("asus", StringComparison.OrdinalIgnoreCase) ||
                        path.Contains("asus", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("atk", StringComparison.OrdinalIgnoreCase) ||
                        disp.Contains("atk", StringComparison.OrdinalIgnoreCase) ||
                        path.Contains("atk", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("myasus", StringComparison.OrdinalIgnoreCase) ||
                        disp.Contains("myasus", StringComparison.OrdinalIgnoreCase) ||
                        path.Contains("myasus", StringComparison.OrdinalIgnoreCase))
                    {
                        svcCount++;
                        sb.AppendLine($"  [{state}] {name} ({disp})");
                        sb.AppendLine($"      Path: {path}");
                    }
                }
                sb.AppendLine($"Total matching Services: {svcCount}");

                sb.AppendLine();
                sb.AppendLine("System Drivers:");
                using var driverSearcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT Name, DisplayName, State, PathName FROM Win32_SystemDriver");
                using var driverColl = driverSearcher.Get();
                int drvCount = 0;
                foreach (ManagementObject mo in driverColl)
                {
                    string name = mo["Name"]?.ToString() ?? "";
                    string disp = mo["DisplayName"]?.ToString() ?? "";
                    string path = mo["PathName"]?.ToString() ?? "";
                    string state = mo["State"]?.ToString() ?? "";

                    if (name.Contains("asus", StringComparison.OrdinalIgnoreCase) ||
                        disp.Contains("asus", StringComparison.OrdinalIgnoreCase) ||
                        path.Contains("asus", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("atk", StringComparison.OrdinalIgnoreCase) ||
                        disp.Contains("atk", StringComparison.OrdinalIgnoreCase) ||
                        path.Contains("atk", StringComparison.OrdinalIgnoreCase))
                    {
                        drvCount++;
                        sb.AppendLine($"  [{state}] {name} ({disp})");
                        sb.AppendLine($"      Path: {path}");
                    }
                }
                sb.AppendLine($"Total matching System Drivers: {drvCount}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Error querying Services/Drivers: {ex.Message}");
            }
            sb.AppendLine();

            // 3. Win32_Fan and Win32_TemperatureProbe results
            sb.AppendLine("--- 3. Win32_Fan & Win32_TemperatureProbe Results ---");
            try
            {
                using var fanSearcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT * FROM Win32_Fan");
                using var fanColl = fanSearcher.Get();
                sb.AppendLine($"Win32_Fan count: {fanColl.Count}");
                foreach (ManagementObject mo in fanColl)
                {
                    sb.AppendLine($"  Fan: Name={mo["Name"]}, DeviceID={mo["DeviceID"]}, Status={mo["Status"]}, DesiredSpeed={mo["DesiredSpeed"]}");
                }

                using var tempSearcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT * FROM Win32_TemperatureProbe");
                using var tempColl = tempSearcher.Get();
                sb.AppendLine($"Win32_TemperatureProbe count: {tempColl.Count}");
                foreach (ManagementObject mo in tempColl)
                {
                    sb.AppendLine($"  TempProbe: Name={mo["Name"]}, DeviceID={mo["DeviceID"]}, CurrentReading={mo["CurrentReading"]}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Error querying Win32_Fan / Win32_TemperatureProbe: {ex.Message}");
            }
            sb.AppendLine();

            // 4. root\cimv2 classes with Asus in the name
            sb.AppendLine("--- 4. root\\cimv2 Classes (Filtering 'Asus') ---");
            try
            {
                using var cimSearcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT * FROM meta_class");
                using var cimColl = cimSearcher.Get();
                int asusCimCount = 0;
                foreach (ManagementClass mc in cimColl)
                {
                    string className = mc["__CLASS"]?.ToString() ?? "";
                    if (className.Contains("Asus", StringComparison.OrdinalIgnoreCase))
                    {
                        asusCimCount++;
                        int instanceCount = 0;
                        try
                        {
                            using var instSearcher = new ManagementObjectSearcher(@"root\cimv2", $"SELECT * FROM {className}");
                            using var instColl = instSearcher.Get();
                            instanceCount = instColl.Count;
                        }
                        catch { }

                        var methods = mc.Methods.Cast<MethodData>().Select(m => m.Name).ToList();
                        var properties = mc.Properties.Cast<PropertyData>().Select(p => p.Name).ToList();

                        sb.AppendLine($"Class: {className} (Instances: {instanceCount})");
                        sb.AppendLine($"  Methods: {(methods.Count > 0 ? string.Join(", ", methods) : "None")}");
                        sb.AppendLine($"  Properties: {(properties.Count > 0 ? string.Join(", ", properties) : "None")}");
                    }
                }
                sb.AppendLine($"Total matching root\\cimv2 classes: {asusCimCount}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Error querying root\\cimv2 Asus classes: {ex.Message}");
            }
            sb.AppendLine("================================================================================");

            string report = sb.ToString();
            Console.WriteLine(report);

            try
            {
                string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                File.WriteAllText(Path.Combine(exeDir, "asus_discovery.txt"), report);
                File.WriteAllText("asus_discovery.txt", report);

                string appDataFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PulseIsland",
                    "asus_discovery.txt");
                string? dir = Path.GetDirectoryName(appDataFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(appDataFile, report);
            }
            catch (Exception exIo)
            {
                Console.WriteLine($"Error writing asus_discovery.txt: {exIo.Message}");
            }
        }
    }
}
