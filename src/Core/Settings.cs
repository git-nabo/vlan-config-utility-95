using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace VlanConfig95.Core
{
    /// <summary>
    /// User preferences persisted in a small INI-style file under the user's profile.
    ///
    /// Logging is OFF by default: no log file, and no log directory, is created until
    /// the user explicitly enables it in File -> Options.
    /// </summary>
    public static class Settings
    {
        private static readonly object Sync = new object();
        private static bool _loaded;

        /// <summary>When false, nothing is written to disk at all.</summary>
        public static bool LoggingEnabled { get; set; }

        /// <summary>Directory holding the settings file and (optionally) the log.</summary>
        public static string DirectoryPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VlanConfig95");
            }
        }

        public static string SettingsPath
        {
            get { return Path.Combine(DirectoryPath, "settings.ini"); }
        }

        /// <summary>Reads the settings file. Safe to call repeatedly; never throws.</summary>
        public static void Load()
        {
            lock (Sync)
            {
                if (_loaded)
                    return;

                _loaded = true;
                LoggingEnabled = false;   // default: logging is opt-in

                try
                {
                    if (!File.Exists(SettingsPath))
                        return;

                    foreach (string raw in File.ReadAllLines(SettingsPath, Encoding.UTF8))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                            continue;

                        int eq = line.IndexOf('=');
                        if (eq <= 0)
                            continue;

                        string key = line.Substring(0, eq).Trim();
                        string value = line.Substring(eq + 1).Trim();

                        if (string.Equals(key, "LoggingEnabled", StringComparison.OrdinalIgnoreCase))
                        {
                            bool parsed;
                            LoggingEnabled = bool.TryParse(value, out parsed) && parsed;
                        }
                    }
                }
                catch
                {
                    // Corrupt or unreadable settings must never stop the app starting.
                    LoggingEnabled = false;
                }
            }
        }

        /// <summary>Writes the settings file. Only called when the user changes a setting.</summary>
        public static void Save()
        {
            lock (Sync)
            {
                try
                {
                    if (!Directory.Exists(DirectoryPath))
                        Directory.CreateDirectory(DirectoryPath);

                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("# VLAN Configuration Utility settings");
                    sb.AppendLine("# Set to true to write a log file. Default is false.");
                    sb.AppendLine("LoggingEnabled=" + LoggingEnabled.ToString(CultureInfo.InvariantCulture));

                    File.WriteAllText(SettingsPath, sb.ToString(), Encoding.UTF8);
                }
                catch
                {
                    // If we cannot persist the preference the app still keeps running.
                }
            }
        }
    }
}