using System;
using PulseIsland.Models;

namespace PulseIsland.Services
{
    public interface ISensorService : IDisposable
    {
        bool IsRunning { get; }
        bool IsPaused { get; }

        event Action<HardwareMetrics>? MetricsUpdated;

        void Start();
        void Stop();
        void Pause();
        void Resume();

        HardwareMetrics GetCurrentMetrics();
        string GenerateDiagnosticsReport();
        void DumpDiagnostics(string? targetFilePath = null);
    }
}

