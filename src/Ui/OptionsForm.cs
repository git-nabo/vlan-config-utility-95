using System;
using System.Drawing;
using System.Windows.Forms;
using VlanConfig95.Core;

namespace VlanConfig95.Ui
{
    /// <summary>
    /// File -> Options. Currently holds the persistent "Enable logging" switch.
    /// The setting is stored per-user in %LOCALAPPDATA%\VlanConfig95\settings.ini and
    /// does not require administrator rights.
    /// </summary>
    public sealed class OptionsForm : Win95Form
    {
        private readonly ClassicCheckBox _loggingBox;
        private readonly Label _logPathLabel;

        public OptionsForm()
        {
            BuildChrome("VLAN Configuration - Options");
            CloseOnEscape = true;

            Width = 430;
            Height = 250;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;

            Panel root = new Panel();
            root.Dock = DockStyle.Fill;
            root.BackColor = Win95Style.Face;
            root.Padding = new Padding(12, 12, 12, 12);
            ClientSurface.Controls.Add(root);

            ClassicGroupBox options = new ClassicGroupBox();
            options.Text = "Options";
            options.SetBounds(10, 10, 380, 92);
            root.Controls.Add(options);

            _loggingBox = new ClassicCheckBox();
            _loggingBox.Text = "Enable logging";
            _loggingBox.SetBounds(14, 24, 200, 16);
            _loggingBox.Checked = Log.Enabled;
            options.Controls.Add(_loggingBox);

            Label hint = new Label();
            hint.Text = "When disabled, no log file or log directory is created.";
            hint.Font = Win95Style.CreateFontSafe();
            hint.BackColor = Win95Style.Face;
            hint.ForeColor = Win95Style.WindowText;
            hint.AutoSize = false;
            hint.SetBounds(14, 46, 350, 18);
            options.Controls.Add(hint);

            _logPathLabel = new Label();
            _logPathLabel.Font = Win95Style.CreateFontSafe();
            _logPathLabel.BackColor = Win95Style.Face;
            _logPathLabel.ForeColor = Win95Style.WindowText;
            _logPathLabel.AutoSize = false;
            _logPathLabel.SetBounds(16, 66, 360, 30);
            _logPathLabel.Text = LogLocationText();
            options.Controls.Add(_logPathLabel);

            // Reflect live changes in the hint while the dialog is open.
            _loggingBox.CheckedChanged += delegate
            {
                _logPathLabel.Text = LogLocationText(_loggingBox.Checked);
            };

            Panel bar = new Panel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = 37;
            bar.BackColor = Win95Style.Face;

            ClassicButton ok = new ClassicButton();
            ok.Text = "OK";
            ok.IsDefaultButton = true;
            ok.SetBounds(bar.Width - 186, 7, 90, 23);
            ok.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            ok.Click += delegate { SaveAndClose(); };
            bar.Controls.Add(ok);

            ClassicButton cancel = new ClassicButton();
            cancel.Text = "Cancel";
            cancel.IsCancelButton = true;
            cancel.SetBounds(bar.Width - 86, 7, 90, 23);
            cancel.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            cancel.Click += delegate { Close(); };
            bar.Controls.Add(cancel);

            ClientSurface.Controls.Add(bar);
        }

        private static string LogLocationText()
        {
            return LogLocationText(Log.Enabled);
        }

        private static string LogLocationText(bool enabled)
        {
            if (!enabled)
                return "Log file: (logging disabled)";

            string path = Log.FilePath;
            return "Log file:\n" + (string.IsNullOrEmpty(path) ? "(not created yet)" : path);
        }

        private void SaveAndClose()
        {
            bool desired = _loggingBox.Checked;

            if (desired != Log.Enabled)
            {
                Log.Enabled = desired;
                Settings.Save();
            }

            Close();
        }
    }
}