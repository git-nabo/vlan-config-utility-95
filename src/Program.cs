using System;
using System.Threading;
using System.Windows.Forms;
using VlanConfig95.Core;
using VlanConfig95.Ui;

namespace VlanConfig95
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Load user preferences first. Logging is off unless the user enabled it, so no
            // log file or log directory is created on a default run.
            Settings.Load();
            Log.EnsureInitialised();

            bool createdNew;
            using (Mutex single = new Mutex(true, @"Local\VlanConfig95.SingleInstance", out createdNew))
            {
                // An elevated relaunch starts while this process is still shutting
                // down and still holds the mutex. Give it a moment to go away rather
                // than telling the user the app is already running and exiting - that
                // left them with no window at all after choosing "Restart as Admin".
                if (!createdNew && !TryBecomePrimaryInstance(single))
                {
                    Win95MessageBox.ShowInfo(null, "VLAN Configuration Utility",
                        "VLAN Configuration Utility is already running.");
                    return 0;
                }

                try
                {
                    Application.Run(new MainForm());
                }
                catch (Exception ex)
                {
                    // Never let a raw stack trace reach the user.
                    ShowFatal(ex);
                    return 1;
                }
                finally
                {
                    try { single.ReleaseMutex(); } catch { }
                }
            }

            return 0;
        }

        /// <summary>
        /// Waits for the existing instance to release the single-instance mutex and
        /// takes ownership of it. Returns false if another instance keeps holding it.
        /// </summary>
        private static bool TryBecomePrimaryInstance(Mutex single)
        {
            const int Attempts = 30;
            const int WaitMs = 200;

            for (int attempt = 0; attempt < Attempts; attempt++)
            {
                try
                {
                    if (single.WaitOne(WaitMs, false))
                    {
                        single.ReleaseMutex();
                        single.WaitOne(WaitMs, false);   // re-acquire for the using block
                        return true;
                    }
                }
                catch (AbandonedMutexException)
                {
                    // The previous owner exited without releasing: we now own it.
                    try { single.ReleaseMutex(); } catch { }
                    single.WaitOne(WaitMs, false);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static void ShowFatal(Exception ex)
        {
            try
            {
                string detail = ex.GetType().Name + ": " + ex.Message;
                if (ex.InnerException != null)
                    detail += Environment.NewLine + "Inner: " + ex.InnerException.Message;

                Win95MessageBox.ShowError(null, "VLAN Configuration Utility",
                    "The utility could not continue because of an unexpected error.", detail);

                Log.Write("FATAL", "-", "-", "-", ex.GetType().Name + ": " + ex.Message);
            }
            catch
            {
                // If even the dialog fails there is nothing more we can do.
            }
        }
    }
}