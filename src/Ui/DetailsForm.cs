using System;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using VlanConfig95.Core;

namespace VlanConfig95.Ui
{
    /// <summary>
    /// The [ Details &gt;&gt; ] diagnostic window: shows the exact PowerShell commands,
    /// their raw output, VLAN candidate scoring decisions and the log tail.
    /// </summary>
    public sealed class DetailsForm : Win95Form
    {
        private readonly NetworkService _service;
        private TextBox _output;
        private TablessTabControl _tabs;

        public DetailsForm(NetworkService service, AdapterInfo adapter, VlanSupport support)
        {
            _service = service;

            BuildChrome("VLAN Configuration - Details");
            // Escape and Alt+F4 both close this dialog.
            CloseOnEscape = true;

            Width = 720;
            Height = 520;
            // Center on the parent window, like a classic Win95 dialog.
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;

            TablessTabControl tabs = new TablessTabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.Padding = new Padding(12, 4, 12, 4);
            tabs.Font = Win95Style.CreateFontSafe();
            _tabs = tabs;

            _output = new TextBox();
            _output.Multiline = true;
            _output.ReadOnly = true;
            _output.ScrollBars = ScrollBars.Both;
            _output.WordWrap = false;
            _output.BackColor = Win95Style.FieldBack;
            _output.ForeColor = Win95Style.FieldText;
            _output.Font = new Font("Consolas", 8.5f);
            _output.Dock = DockStyle.Fill;
            _output.Text = BuildReport(adapter, support);

            SunkenPanel host = new SunkenPanel();
            host.Dock = DockStyle.Fill;
            host.Padding = new Padding(3);
            host.Controls.Add(_output);

            tabs.AddPage(new TablessPage("Command Output", host));
            tabs.AddPage(new TablessPage("VLAN Analysis", MakeTextPanel(BuildReport(adapter, support))));
            tabs.AddPage(new TablessPage("Log File", MakeTextPanel(Log.ReadTail(120))));
            tabs.EnsureFirstPageVisible();

            ClientSurface.Controls.Add(tabs);

            Panel bar = new Panel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = 37;
            bar.BackColor = Win95Style.Face;

            ClassicButton copyButton = new ClassicButton();
            copyButton.Text = "Copy All";
            copyButton.SetBounds(bar.Width - 260, 7, 80, 23);
            copyButton.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            copyButton.Click += delegate { CopyToClipboard(); };
            bar.Controls.Add(copyButton);

            ClassicButton closeButton = new ClassicButton();
            closeButton.Text = "Close";
            closeButton.SetBounds(bar.Width - 170, 7, 80, 23);
            closeButton.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            closeButton.IsDefaultButton = true;
            closeButton.Click += delegate { Close(); };
            bar.Controls.Add(closeButton);

            ClientSurface.Controls.Add(bar);
        }

        /// <summary>Wraps text in a sunken, read-only panel suitable for a tab.</summary>
        private static Control MakeTextPanel(string text)
        {
            TextBox box = new TextBox();
            box.Multiline = true;
            box.ReadOnly = true;
            box.ScrollBars = ScrollBars.Both;
            box.WordWrap = false;
            box.BackColor = Win95Style.FieldBack;
            box.ForeColor = Win95Style.FieldText;
            box.Font = new Font("Consolas", 8.5f);
            box.Dock = DockStyle.Fill;
            box.Text = text;

            SunkenPanel host = new SunkenPanel();
            host.Dock = DockStyle.Fill;
            host.Padding = new Padding(3);
            host.Controls.Add(box);
            return host;
        }

        private void CopyToClipboard()
        {
            try
            {
                Clipboard.SetText(_output.Text);
            }
            catch (Exception ex)
            {
                Win95MessageBox.ShowError(this, "Details",
                    "The contents could not be copied to the clipboard.", ex.Message);
            }
        }
private string BuildReport(AdapterInfo adapter, VlanSupport support)
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("=== Adapter ===");
            if (adapter != null)
            {
                sb.AppendLine("Name          : " + adapter.Name);
                sb.AppendLine("Description   : " + adapter.Description);
                sb.AppendLine("InterfaceIndex: " + adapter.InterfaceIndex);
                sb.AppendLine("Driver        : " + adapter.DriverProvider + " " + adapter.DriverVersion);
                sb.AppendLine("Category      : " + adapter.CategoryLabel);
            }
            else
            {
                sb.AppendLine("(no adapter selected)");
            }

            sb.AppendLine();
            sb.AppendLine("=== Detection result ===");
            if (support != null)
            {
                sb.AppendLine("Supported        : " + support.Supported);
                sb.AppendLine("Selected property: " +
                    (support.Property != null ? support.Property.RegistryKeyword : "(none)"));
                sb.AppendLine("Current VLAN ID  : " +
                    (support.CurrentVlanId.HasValue
                        ? support.CurrentVlanId.Value.ToString(CultureInfo.InvariantCulture)
                        : "none"));
                sb.AppendLine("CIM VlanID       : " +
                    (support.CimVlanId.HasValue
                        ? support.CimVlanId.Value.ToString(CultureInfo.InvariantCulture)
                        : "not reported"));

                if (support.Candidates.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Accepted VLAN candidates:");
                    foreach (VlanProperty p in support.Candidates)
                    {
                        sb.AppendLine("  - " + p.RegistryKeyword +
                                      "  type=" + (p.DisplayParameterType == string.Empty ? "n/a" : p.DisplayParameterType) +
                                      "  range=" + (p.NumericMin == string.Empty ? "-" : p.NumericMin) + ".." +
                                      (p.NumericMax == string.Empty ? "-" : p.NumericMax));
                    }
                }

                if (support.Rejected.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Rejected (matched 'VLAN' but not a VLAN ID field):");
                    foreach (string r in support.Rejected)
                        sb.AppendLine("  - " + r);
                }
            }

            sb.AppendLine();
            sb.AppendLine("=== Raw PowerShell trace ===");
            sb.AppendLine(_service.LastRawDetails);

            return sb.ToString();
        }
    }
}