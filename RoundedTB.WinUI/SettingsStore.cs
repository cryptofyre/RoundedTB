using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoundedTB.WinUI
{
    /// <summary>
    /// The persisted shape from RoundedTB/Types.cs, copied rather than linked because that file
    /// also carries the taskbar/UIA types this spike does not use. Property names and order match,
    /// so rtb.json round-trips between the two apps.
    /// </summary>
    public class Types
    {
        public class Settings
        {
            public int Version { get; set; }
            public SegmentSettings SimpleTaskbarLayout { get; set; } = new();
            public SegmentSettings DynamicAppListLayout { get; set; } = new();
            public SegmentSettings DynamicTrayLayout { get; set; } = new();
            public SegmentSettings DynamicWidgetsLayout { get; set; } = new();
            public SegmentSettings DynamicSecondaryClockLayout { get; set; } = new();
            public int WidgetsWidth { get; set; }
            public int ClockWidth { get; set; }
            public bool IsDynamic { get; set; }
            public bool IsCentred { get; set; }
            public bool IsWindows11 { get; set; }
            public bool ShowTray { get; set; }
            public bool ShowWidgets { get; set; }
            public bool ShowSecondaryClock { get; set; }
            public bool CompositionCompat { get; set; }
            public bool IsNotFirstLaunch { get; set; }
            public bool FillOnMaximise { get; set; }
            public bool FillOnTaskSwitch { get; set; }
            public bool ShowSegmentsOnHover { get; set; }
            public int AutoHide { get; set; }
        }

        public class SegmentSettings
        {
            public int CornerRadius { get; set; }
            public int MarginTop { get; set; }
            public int MarginLeft { get; set; }
            public int MarginBottom { get; set; }
            public int MarginRight { get; set; }
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(Types.Settings))]
    internal partial class SettingsJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// Reads and writes %LOCALAPPDATA%\RoundedTB\rtb.json. No legacy migration: the WPF app has
    /// already migrated any file this spike will see.
    /// </summary>
    internal static class SettingsStore
    {
        public static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoundedTB", "rtb.json");

        public static Types.Settings Read()
        {
            try
            {
                string json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.Settings) ?? CreateDefaults();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to read settings, using defaults: {ex.Message}");
                return CreateDefaults();
            }
        }

        public static void Write(Types.Settings settings)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            string temp = ConfigPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.Settings));
            File.Move(temp, ConfigPath, overwrite: true);
        }

        private static Types.SegmentSettings DefaultLayout() =>
            new() { CornerRadius = 7, MarginLeft = 3, MarginTop = 3, MarginRight = 3, MarginBottom = 3 };

        /// <summary>The Windows 11 defaults from Interaction.CreateDefaultSettings.</summary>
        public static Types.Settings CreateDefaults() => new()
        {
            SimpleTaskbarLayout = DefaultLayout(),
            DynamicAppListLayout = DefaultLayout(),
            DynamicTrayLayout = DefaultLayout(),
            DynamicWidgetsLayout = DefaultLayout(),
            DynamicSecondaryClockLayout = DefaultLayout(),
            WidgetsWidth = 168,
            ClockWidth = 110,
            IsWindows11 = true,
            FillOnMaximise = true,
            FillOnTaskSwitch = true,
        };
    }
}
