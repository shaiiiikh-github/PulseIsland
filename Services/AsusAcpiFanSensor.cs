using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PulseIsland.Services
{
    public class AsusAcpiFanProbeResult
    {
        public bool DeviceOpened { get; set; }
        public int? RawCpuValue { get; set; }
        public float? CpuFanRpm { get; set; }
        public int? RawGpuValue { get; set; }
        public float? GpuFanRpm { get; set; }
        public bool IsAvailable { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
    }

    /// <summary>
    /// Direct ASUS ACPI driver sensor interface via \\.\ATKACPI and IOCTL 0x0022240C.
    /// Uses ASUS DSTS method with device IDs 0x00110013 (CPU fan) and 0x00110014 (GPU fan).
    /// Decoding rule: if ((value & 0x10000) != 0) RPM = (value & 0xFFFF) * 100; else unavailable.
    /// </summary>
    public class AsusAcpiFanSensor : IDisposable
    {
        private const string DevicePath = @"\\.\ATKACPI";
        private const uint IOCTL_ATKACPI = 0x0022240C;
        private const uint DSTS = 0x53545344; // 'D' 'S' 'T' 'S' in little-endian ASCII
        public const uint CPU_FAN_DEVID = 0x00110013;
        public const uint GPU_FAN_DEVID = 0x00110014;

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

        private SafeFileHandle? _deviceHandle;
        private bool _deviceOpenFailed;

        public AsusAcpiFanProbeResult LastProbeResult { get; private set; } = new();

        public AsusAcpiFanProbeResult Probe()
        {
            var result = new AsusAcpiFanProbeResult();

            try
            {
                var handle = EnsureHandle();
                if (handle == null || handle.IsInvalid)
                {
                    result.DeviceOpened = false;
                    result.IsAvailable = false;
                    result.StatusMessage = "Unable to open \\\\.\\ATKACPI device.";
                    LastProbeResult = result;
                    return result;
                }

                result.DeviceOpened = true;

                // Probe CPU Fan (0x00110013)
                if (CallDsts(handle, CPU_FAN_DEVID, out int rawCpu))
                {
                    result.RawCpuValue = rawCpu;
                    if (TryDecodeRpm(rawCpu, out float cpuRpm))
                    {
                        result.CpuFanRpm = cpuRpm;
                        result.IsAvailable = true;
                    }
                }

                // Probe GPU Fan (0x00110014) if present
                if (CallDsts(handle, GPU_FAN_DEVID, out int rawGpu))
                {
                    result.RawGpuValue = rawGpu;
                    if (TryDecodeRpm(rawGpu, out float gpuRpm))
                    {
                        result.GpuFanRpm = gpuRpm;
                        result.IsAvailable = true;
                    }
                }

                if (result.IsAvailable)
                {
                    result.StatusMessage = $"Success. CPU Fan Raw: 0x{result.RawCpuValue:X8} ({result.CpuFanRpm} RPM)";
                }
                else
                {
                    result.StatusMessage = "Device responded but returned unavailable (0x10000 flag not set).";
                }
            }
            catch (Exception ex)
            {
                result.DeviceOpened = false;
                result.IsAvailable = false;
                result.StatusMessage = $"Exception probing \\\\.\\ATKACPI: {ex.Message}";
            }

            LastProbeResult = result;
            return result;
        }

        public float? ReadFanRpm()
        {
            if (_deviceOpenFailed)
            {
                return null;
            }

            try
            {
                var handle = EnsureHandle();
                if (handle == null || handle.IsInvalid)
                {
                    return null;
                }

                // Read CPU Fan first (0x00110013)
                if (CallDsts(handle, CPU_FAN_DEVID, out int rawCpu) && TryDecodeRpm(rawCpu, out float cpuRpm))
                {
                    return cpuRpm;
                }

                // Try GPU Fan (0x00110014)
                if (CallDsts(handle, GPU_FAN_DEVID, out int rawGpu) && TryDecodeRpm(rawGpu, out float gpuRpm))
                {
                    return gpuRpm;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AsusAcpiFanSensor read error: {ex.Message}");
            }

            return null;
        }

        public static bool TryDecodeRpm(long raw, out float rpm)
        {
            rpm = 0f;
            uint val = unchecked((uint)raw);

            // Treat 0xFFFFFFFE (-2) and 0xFFFFFFFF (-1) as unsupported
            if (val == 0xFFFFFFFE || val == 0xFFFFFFFF || val == 0)
            {
                return false;
            }

            // A reading is valid ONLY if (value & 0xFFFF0000) == 0x00010000
            if ((val & 0xFFFF0000) == 0x00010000)
            {
                int speedHundreds = (int)(val & 0xFFFF);
                float computedRpm = speedHundreds * 100f;

                // RPM must be within 0..10000
                if (computedRpm >= 0f && computedRpm <= 10000f)
                {
                    rpm = computedRpm;
                    return true;
                }
            }

            return false;
        }

        private SafeFileHandle? EnsureHandle()
        {
            if (_deviceOpenFailed) return null;

            if (_deviceHandle != null && !_deviceHandle.IsInvalid && !_deviceHandle.IsClosed)
            {
                return _deviceHandle;
            }

            try
            {
                var handle = CreateFile(
                    DevicePath,
                    GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    FILE_ATTRIBUTE_NORMAL,
                    IntPtr.Zero);

                if (handle.IsInvalid)
                {
                    _deviceOpenFailed = true;
                    return null;
                }

                _deviceHandle = handle;
                return _deviceHandle;
            }
            catch
            {
                _deviceOpenFailed = true;
                return null;
            }
        }

        private bool CallDsts(SafeFileHandle handle, uint deviceId, out int rawValue)
        {
            rawValue = 0;

            try
            {
                // Construct ACPI input buffer:
                // Header: MethodID (4 bytes), args length (4 bytes)
                // Args: deviceId (4 bytes), padding/status (4 bytes)
                byte[] inBuffer = new byte[16];
                BitConverter.GetBytes(DSTS).CopyTo(inBuffer, 0);
                BitConverter.GetBytes(8).CopyTo(inBuffer, 4);
                BitConverter.GetBytes(deviceId).CopyTo(inBuffer, 8);
                BitConverter.GetBytes(0).CopyTo(inBuffer, 12);

                byte[] outBuffer = new byte[16];
                uint bytesReturned = 0;

                bool success = DeviceIoControl(
                    handle,
                    IOCTL_ATKACPI,
                    inBuffer,
                    (uint)inBuffer.Length,
                    outBuffer,
                    (uint)outBuffer.Length,
                    ref bytesReturned,
                    IntPtr.Zero);

                if (success)
                {
                    rawValue = BitConverter.ToInt32(outBuffer, 0);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DeviceIoControl error: {ex.Message}");
            }

            return false;
        }

        public void Dispose()
        {
            if (_deviceHandle != null && !_deviceHandle.IsClosed)
            {
                _deviceHandle.Dispose();
                _deviceHandle = null;
            }
        }

        #region P/Invoke
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            byte[] lpInBuffer,
            uint nInBufferSize,
            byte[] lpOutBuffer,
            uint nOutBufferSize,
            ref uint lpBytesReturned,
            IntPtr lpOverlapped);
        #endregion
    }
}
