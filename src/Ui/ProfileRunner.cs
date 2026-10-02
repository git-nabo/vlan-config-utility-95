using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using VlanConfig95.Core;

namespace VlanConfig95.Ui
{
    /// <summary>
    /// Runs long profile operations on a background thread so the window stays
    /// responsive while a driver resets the adapter.
    ///
    /// Applying a profile can drop the network connection for a few seconds, so the
    /// UI shows a small classic progress dialog and disables further applies until it
    /// finishes.
    /// </summary>
    public static class ProfileRunner
    {
        /// <summary>True while an operation is in progress (blocks a second apply).</summary>
        public static bool IsBusy { get; private set; }

        private static Win95Form _progress;

        /// <summary>Applies a profile asynchronously, reporting progress in the status bar.</summary>
        public static void Run(Form owner, NetworkService service, AdapterInfo adapter,
                               VlanSupport support, NetworkProfile profile, bool allowRestore)
        {
            if (IsBusy)
            {
                Win95MessageBox.ShowWarning(owner, "Network Profiles",
                    "An operation is already in progress.", "Please wait for it to finish.");
                return;
            }

            if (!Elevation.IsElevated())
            {
                bool elevate = Win95MessageBox.Ask(owner, "Administrator Privileges",
                    "Administrator privileges are required to change adapter settings.",
                    "The profile could not be applied because this utility is not running elevated.\n\n" +
                    "Yes - restart with administrator privileges");

                if (elevate) RequestElevation(owner);
                return;
            }

            IsBusy = true;
            RefreshOwnerButtons(owner);          // grey out Apply/Restore/Save at once
            ProfileApplier applier = new ProfileApplier(service);
            SetStatus(owner, "Applying profile '" + profile.Name + "'...");
            ShowProgress(true, "Applying profile '" + profile.Name + "'...");

            ThreadPool.QueueUserWorkItem(delegate
            {
                ApplyReport report = null;
                try
                {
                    ProfileApplier.ProgressHandler handler = delegate(string message)
                    {
                        SetStatusSafe(owner, message);
                        return true;
                    };
                    report = applier.Apply(adapter, profile, support, handler);
                }
                catch (Exception ex)
                {
                    report = new ApplyReport();
                    report.Success = false;
                    report.Message = "The profile could not be applied: " + ex.Message;
                }

                ApplyReport final = report;
                AdapterApplierHolder = applier;

                // IsBusy must ALWAYS be cleared, even if the owner window has gone
                // away: otherwise every later attempt is refused with "an operation
                // is already in progress" forever.
                try
                {
                    owner.BeginInvoke(new MethodInvoker(delegate
                    {
                        IsBusy = false;
                        ShowProgress(false);
                        SetStatus(owner, final.Message);
                        ShowReport(owner, final);
                        RefreshOwnerButtons(owner);
                    }));
                }
                catch (InvalidOperationException)
                {
                    IsBusy = false;
                    ShowProgress(false);
                }
            });
        }

        /// <summary>
        /// Re-enables Apply/Restore/Save once the operation has finished. The main
        /// window disables them while a run is in flight, so they have to be restored
        /// here or the buttons would stay greyed out after the first apply.
        /// </summary>
        private static void RefreshOwnerButtons(Form owner)
        {
            MainForm main = owner as MainForm;
            if (main != null) main.UpdateProfileButtons();
        }

        /// <summary>Last applier, kept so the main window can offer "restore previous".</summary>
        public static ProfileApplier AdapterApplierHolder { get; private set; }

        /// <summary>Captures the adapter's current configuration as a new profile.</summary>
        public static void RunCapture(Form owner, NetworkService service, AdapterInfo adapter,
                                      VlanSupport support, Action<NetworkProfile> onCaptured)
        {
            if (IsBusy) return;
            IsBusy = true;

            SetStatus(owner, "Reading adapter configuration...");
            ShowProgress(true, "Reading adapter configuration...");

            ThreadPool.QueueUserWorkItem(delegate
            {
                string error;
                ProfileApplier applier = new ProfileApplier(service);
                AdapterSnapshot snap = applier.ReadState(adapter, out error);
                NetworkProfile created = null;

                if (snap != null)
                {
                    created = new NetworkProfile();
                    created.Name = ProfileStore.UniqueName(adapter.Name);
                    created.IpConfig = snap.IsDhcp ? IpMode.Dhcp : IpMode.Static;
                    created.IpAddress = snap.IsDhcp ? string.Empty : snap.IpAddress;
                    created.PrefixLength = (snap.IsDhcp || snap.PrefixLength <= 0)
                        ? string.Empty
                        : snap.PrefixLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    created.SubnetMask = (snap.IsDhcp || snap.PrefixLength <= 0)
                        ? string.Empty
                        : NetworkProfile.PrefixToMask(snap.PrefixLength);
                    created.Gateway = snap.Gateway;
                    created.DnsConfig = string.IsNullOrEmpty(snap.PrimaryDns) ? DnsMode.Automatic : DnsMode.Static;
                    created.PrimaryDns = snap.PrimaryDns;
                    created.SecondaryDns = snap.SecondaryDns;
                    created.Description = "Captured from " + adapter.Name;
                }

                NetworkProfile result = created;
                string err = error;

                owner.BeginInvoke(new MethodInvoker(delegate
                {
                    IsBusy = false;
                    ShowProgress(false);
                    SetStatus(owner, "Ready.");

                    if (result == null)
                    {
                        Win95MessageBox.ShowError(owner, "Network Profiles",
                            "The current configuration could not be read.", err);
                        return;
                    }

                    onCaptured(result);
                }));
            });
        }

        private static void ShowReport(Form owner, ApplyReport report)
        {
            System.Text.StringBuilder detail = new System.Text.StringBuilder();
            foreach (string step in report.Steps)
                detail.AppendLine(step);

            if (report.Warnings.Count > 0)
            {
                detail.AppendLine();
                detail.AppendLine("Warnings:");
                foreach (string w in report.Warnings)
                    detail.AppendLine("  " + w);
            }

            if (report.Success && !report.Partial)
                Win95MessageBox.ShowInfo(owner, "Network Profiles", report.Message, detail.ToString());
            else if (report.Success)
                Win95MessageBox.ShowWarning(owner, "Network Profiles", report.Message, detail.ToString());
            else
                Win95MessageBox.ShowError(owner, "Network Profiles", report.Message, detail.ToString());
        }

        public static void RequestElevation(Form owner)
        {
            // Let the elevated instance take over the single-instance mutex.
            Elevation.ReleaseSingleInstance();

            if (Elevation.RelaunchElevated())
            {
                if (owner != null) owner.Close();
                return;
            }

            // Cancelled: keep running and keep guarding the mutex.
            Elevation.RelaunchPending = false;

            if (owner != null)
            {
                Win95MessageBox.ShowWarning(owner, "Administrator Privileges",
                    "The elevation request was cancelled or could not be completed.",
                    "The utility is still running without administrator privileges, so adapter settings cannot be changed.");
            }
        }

        private static void SetStatus(Form owner, string message)
        {
            if (owner == null) return;

            MethodInfoish mi = null;
            try
            {
                System.Reflection.MethodInfo method = owner.GetType().GetMethod("SetStatus",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (method != null)
                {
                    object[] args = new object[] { message, "" };
                    mi = new MethodInfoish(owner, method, args);
                    mi.Invoke();
                }
            }
            catch
            {
                // Status text is cosmetic; never let it break an operation.
            }
        }

        private static void SetStatusSafe(Form owner, string message)
        {
            if (owner == null || owner.IsDisposed) return;

            try
            {
                if (owner.InvokeRequired) owner.BeginInvoke(new MethodInvoker(delegate { SetStatus(owner, message); }));
                else SetStatus(owner, message);
            }
            catch
            {
                // Cosmetic only.
            }
        }

        /// <summary>Small wrapper that marshals a private SetStatus call onto the UI thread.</summary>
        private sealed class MethodInfoish
        {
            private readonly Form _owner;
            private readonly System.Reflection.MethodInfo _method;
            private readonly object[] _args;

            public MethodInfoish(Form owner, System.Reflection.MethodInfo method, object[] args)
            {
                _owner = owner; _method = method; _args = args;
            }

            public void Invoke()
            {
                if (_owner.IsDisposed) return;
                if (_owner.InvokeRequired) { _owner.BeginInvoke(new MethodInvoker(Invoke)); return; }
                _method.Invoke(_owner, _args);
            }
        }

        /// <summary>Shows or hides the modal progress window.</summary>
        public static void ShowProgress(bool visible)
        {
            ShowProgress(visible, "Applying profile...");
        }

        public static void ShowProgress(bool visible, string message)
        {
            if (visible)
            {
                if (_progress != null && !_progress.IsDisposed) return;

                _progress = new Win95Form();
                _progress.BuildChrome("Please wait...");
                _progress.StartPosition = FormStartPosition.CenterScreen;
                _progress.Size = new Size(360, 130);
                _progress.MinimizeBox = false;
                _progress.MaximizeBox = false;
                _progress.ShowInTaskbar = false;

                // Do NOT set FormBorderStyle here. BuildChrome already made the window
                // borderless so our own caption bar is the only one drawn; forcing
                // FixedDialog put Windows' system caption on top of it, producing the
                // "double title bar" look.

                Label l = new Label();
                l.Text = message;
                l.Font = Win95Style.CreateFontSafe();
                l.BackColor = Win95Style.Face;
                l.TextAlign = ContentAlignment.MiddleCenter;
                l.SetBounds(12, 20, 336, 30);
                _progress.ClientSurface.Controls.Add(l);

                Win95StatusBar bar = new Win95StatusBar();
                bar.Dock = DockStyle.Bottom;
                bar.Height = 20;
                bar.SetPanes(message, "Working...");
                _progress.ClientSurface.Controls.Add(bar);

                _progress.Show();
                _progress.TopMost = true;
            }
            else
            {
                if (_progress == null) return;
                try { _progress.Close(); } catch { }
                try { _progress.Dispose(); } catch { }
                _progress = null;
            }
        }
    }
}
