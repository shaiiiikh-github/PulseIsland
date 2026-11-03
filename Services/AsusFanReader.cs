using System;
using System.Diagnostics;
using System.Management;

namespace PulseIsland.Services
{
    /// <summary>
    /// Fallback fan reader for ASUS laptops using ASUS WMI (root\wmi\AsusAtkWmi_WMNB).
    /// Decoding rule: if ((value & 0x10000) != 0) RPM = (value & 0xFFFF) * 100; else unavailable.
    /// Order of sources: LibreHardwareMonitor -> AsusAcpiFanSensor -> AsusFanReader.
    /// </summary>
    public class AsusFanReader : IDisposable
    {
        private const uint CPU_FAN_DEVID = 0x00110013;
        private const uint GPU_FAN_DEVID = 0x00110014;

        private bool _probed;
        private bool _wmiWorking;
        private string? _detectedMethod;

        public string? DetectedMethod => _detectedMethod;
        public bool IsWmiWorking => _wmiWorking;

        public float? TryReadFanRpm()
        {
            if (!_probed)
            {
                ProbeCapabilities();
            }

            if (_wmiWorking)
            {
                var rpm = ReadFromAsusAtkWmi();
                if (rpm.HasValue)
                {
                    return rpm;
                }
            }

            return null;
        }

        public static bool CheckWmiClassExists(out string message)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM AsusAtkWmi_WMNB");
                using var collection = searcher.Get();
                int count = 0;
                foreach (ManagementObject mo in collection)
                {
                    count++;
                }

                if (count > 0)
                {
                    message = $"Class exists ({count} instance(s) found).";
                    return true;
                }

                message = "Class exists but returned 0 instances.";
                return true;
            }
            catch (Exception ex)
            {
                message = $"Not available / Query failed: {ex.Message}";
                return false;
            }
        }

        private void ProbeCapabilities()
        {
            _probed = true;

            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM AsusAtkWmi_WMNB");
                using var collection = searcher.Get();
                foreach (ManagementObject mo in collection)
                {
                    // Test CPU fan DSTS (0x00110013)
                    if (TryInvokeDsts(mo, CPU_FAN_DEVID, out float cpuRpm))
                    {
                        _wmiWorking = true;
                        _detectedMethod = "root\\wmi -> AsusAtkWmi_WMNB.DSTS(0x00110013)";
                        Debug.WriteLine($"Found ASUS WMI Fan: {_detectedMethod} -> {cpuRpm} RPM");
                        return;
                    }

                    // Test GPU fan DSTS (0x00110014)
                    if (TryInvokeDsts(mo, GPU_FAN_DEVID, out float gpuRpm))
                    {
                        _wmiWorking = true;
                        _detectedMethod = "root\\wmi -> AsusAtkWmi_WMNB.DSTS(0x00110014)";
                        Debug.WriteLine($"Found ASUS WMI Fan: {_detectedMethod} -> {gpuRpm} RPM");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AsusAtkWmi_WMNB probe error: {ex.Message}");
            }

            _wmiWorking = false;
            _detectedMethod = null;
        }

        private float? ReadFromAsusAtkWmi()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM AsusAtkWmi_WMNB");
                using var collection = searcher.Get();
                foreach (ManagementObject mo in collection)
                {
                    if (TryInvokeDsts(mo, CPU_FAN_DEVID, out float cpuRpm))
                    {
                        return cpuRpm;
                    }

                    if (TryInvokeDsts(mo, GPU_FAN_DEVID, out float gpuRpm))
                    {
                        return gpuRpm;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ReadFromAsusAtkWmi error: {ex.Message}");
            }

            return null;
        }

        private bool TryInvokeDsts(ManagementObject mo, uint deviceId, out float rpm)
        {
            rpm = 0f;

            try
            {
                using var inParams = mo.GetMethodParameters("DSTS");
                inParams["Device_ID"] = deviceId;

                using var outParams = mo.InvokeMethod("DSTS", inParams, null);
                if (outParams != null && outParams["device_status"] != null)
                {
                    uint status = Convert.ToUInt32(outParams["device_status"]);

                    // Strict single decoding path:
                    // Only valid if (value & 0xFFFF0000) == 0x00010000, RPM = (value & 0xFFFF) * 100 within 0..10000
                    if (AsusAcpiFanSensor.TryDecodeRpm(status, out rpm))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TryInvokeDsts (0x{deviceId:X8}) error: {ex.Message}");
            }

            return false;
        }

        public void Dispose()
        {
        }
    }
}
