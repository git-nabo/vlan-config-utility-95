using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VlanConfig95.Core
{
    /// <summary>
    /// Append-only diagnostic log. Writes timestamp, adapter, operation, previous value,
    /// requested value and the result. Never records credentials or unrelated data.
    /// </summary>
    public static class Log
    {
        private static readonly object Sync = new object();
        private static bool _initialised;
        private static string _path;
        private static bool _failed;

        /// <summary>
        /// Mirrors Settings.LoggingEnabled. When false nothing is created on disk.
        /// </summary>
        public static bool Enabled
        {
            get { return Settings.LoggingEnabled; }
            set
            {
                if (Settings.LoggingEnabled == value)
                    return;

                Settings.LoggingEnabled = value;
                _initialised = false;
                _path = null;
                _failed = false;
            }
        }

        /// <summary>Latest log file path, or null when logging is off/unavailable.</summary>
        public static string FilePath
        {
            get
            {
                if (!Enabled)
                    return null;

                return EnsureInitialised();
            }
        }

        /// <summary>
        /// Resolves (and creates) the log file. Returns null without touching the disk
        /// when logging is disabled.
        /// </summary>
        public static string EnsureInitialised()
        {
            if (!Enabled)
                return null;

            if (_initialised)
                return _path;

            _initialised = true;

            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VlanConfig95");

                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "vlan-config95.log");
            }
            catch
            {
                _failed = true;
                _path = null;
            }

            return _path;
        }

        public static void Write(string operation, string adapter, string previous,
                                string requested, string result)
        {
            // No-op (and no file creation) while logging is disabled.
            if (!Enabled || _failed)
                return;

            if (EnsureInitialised() == null)
                return;

            string line = string.Format(CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss}  {1,-12}  adapter={2}  previous={3}  requested={4}  result={5}",
                DateTime.Now, Sanitise(operation), Sanitise(adapter), Sanitise(previous),
                Sanitise(requested), Sanitise(result));

            try
            {
                lock (Sync)
                {
                    // Cap the log so a long-running utility cannot fill the profile.
                    RollIfTooLarge();

                    File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                _failed = true;
            }
        }

        /// <summary>Reads the tail of the log for the Details window.</summary>
        public static string ReadTail(int maxLines)
        {
            if (!Enabled)
                return "Logging is disabled.\n\nEnable it under File -> Options to record and view this log.";

            EnsureInitialised();
            if (_path == null || !File.Exists(_path))
                return "(no log entries yet)";

            try
            {
                string[] all = File.ReadAllLines(_path, Encoding.UTF8);
                int take = Math.Min(all.Length, Math.Max(1, maxLines));

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Log file: " + _path);
                sb.AppendLine("Showing last " + take + " of " + all.Length + " entries.");
                sb.AppendLine(new string('-', 60));

                for (int i = all.Length - take; i < all.Length; i++)
                    sb.AppendLine(all[i]);

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "Could not read the log file: " + ex.Message;
            }
        }

        private static void RollIfTooLarge()
        {
            try
            {
                FileInfo fi = new FileInfo(_path);
                if (!fi.Exists || fi.Length < 512 * 1024)
                    return;

                string old = _path + ".1";
                if (File.Exists(old))
                    File.Delete(old);

                File.Move(_path, old);
            }
            catch
            {
                // Rotation is best-effort only.
            }
        }

        /// <summary>
        /// Strips control characters and collapses newlines so one log entry stays on
        /// one line regardless of adapter names or driver messages.
        /// </summary>
        private static string Sanitise(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "-";

            string cleaned = Regex.Replace(value, @"[\r\n\t]+", " ");
            cleaned = cleaned.Replace("|", "/");
            cleaned = Regex.Replace(cleaned, @"\s{2,}", " ");

            if (cleaned.Length > 400)
                cleaned = cleaned.Substring(0, 400) + "...";

            return cleaned.Trim();
        }
    }
}