using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using PulseIsland.Models;

namespace PulseIsland.Services
{
    public class SettingsService
    {
        private static readonly string SettingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PulseIsland");

        private static readonly string SettingsFile = Path.Combine(SettingsFolder, "settings.json");
        private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppRegistryName = "PulseIsland";

        public AppSettings CurrentSettings { get; private set; }

        public SettingsService()
        {
            CurrentSettings = LoadSettings();

            // When position is locked, clear any saved X/Y offsets
            if (CurrentSettings.IsPositionLocked)
            {
                CurrentSettings.SavedOffsetX = null;
                CurrentSettings.SavedOffsetY = null;
                SaveSettings();
            }
        }

        public AppSettings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    string json = File.ReadAllText(SettingsFile);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading settings: {ex.Message}");
            }

            return new AppSettings();
        }

        public void SaveSettings()
        {
            try
            {
                if (!Directory.Exists(SettingsFolder))
                {
                    Directory.CreateDirectory(SettingsFolder);
                }

                string json = JsonSerializer.Serialize(CurrentSettings, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(SettingsFile, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving settings: {ex.Message}");
            }
        }

        public void ApplyStartWithWindows(bool enable)
        {
            CurrentSettings.StartWithWindows = enable;
            SaveSettings();

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
                if (key != null)
                {
                    string? exePath = Environment.ProcessPath;
                    if (enable && !string.IsNullOrEmpty(exePath))
                    {
                        key.SetValue(AppRegistryName, $"\"{exePath}\"");
                    }
                    else
                    {
                        key.DeleteValue(AppRegistryName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to set registry run key: {ex.Message}");
            }
        }
    }
}
