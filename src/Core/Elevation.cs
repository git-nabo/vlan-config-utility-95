using System;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Windows.Forms;

namespace VlanConfig95.Core
{
    /// <summary>Detects administrator rights and relaunches the app elevated via UAC.</summary>
    public static class Elevation
    {
        public static bool IsElevated()
        {
            try
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        public static string StatusText()
        {
            return IsElevated()
                ? "Administrator privileges granted."
                : "Not elevated - administrator privileges required.";
        }

        /// <summary>
        /// Relaunches this EXE with the "runas" verb, which triggers a proper UAC
        /// prompt on Windows. Returns false when the user declines or it fails.
        ///
        /// The current process must release its single-instance mutex before the new
        /// elevated one starts, otherwise the new instance sees the mutex held, reports
        /// "already running" and exits - leaving the user with no window at all.
        /// <see cref="ReleaseSingleInstance"/> breaks that deadlock.
        /// </summary>
        public static bool RelaunchElevated()
        {
            try
            {
                string exePath = ResolveExecutablePath();
                if (string.IsNullOrEmpty(exePath))
                    return false;

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exePath;
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                psi.WorkingDirectory = System.IO.Path.GetDirectoryName(exePath);

                Process p = Process.Start(psi);
                if (p != null)
                {
                    p.Dispose();
                    return true;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // ERROR_CANCELLED (1223) - the user refused the UAC prompt.
                return false;
            }
            catch
            {
                return false;
            }

            return false;
        }

        /// <summary>
        /// The path of the running EXE. Assembly.Location is empty for single-file
        /// hosts, so the process image path is used as a fallback.
        /// </summary>
        private static string ResolveExecutablePath()
        {
            try
            {
                string location = Assembly.GetEntryAssembly() != null
                    ? Assembly.GetEntryAssembly().Location
                    : null;

                if (!string.IsNullOrEmpty(location) && System.IO.File.Exists(location))
                    return location;

                string exe = Application.ExecutablePath;
                if (!string.IsNullOrEmpty(exe) && System.IO.File.Exists(exe))
                    return exe;
            }
            catch
            {
                // Fall through.
            }

            return null;
        }

        /// <summary>
        /// Signals that this process is about to hand over to an elevated instance.
        /// Program.cs watches this so the new window opens instead of colliding with
        /// the mutex this process still holds.
        /// </summary>
        public static void ReleaseSingleInstance()
        {
            RelaunchPending = true;
        }

        /// <summary>
        /// True once <see cref="ReleaseSingleInstance"/> has been called. Reset when
        /// the elevation turns out to have been cancelled, so the process keeps
        /// guarding the single-instance mutex.
        /// </summary>
        public static bool RelaunchPending { get; set; }
    }
}