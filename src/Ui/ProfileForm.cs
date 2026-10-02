using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using VlanConfig95.Core;

namespace VlanConfig95.Ui
{
    /// <summary>
    /// Windows 95 style profile manager: a classic list of saved profiles on the left
    /// and an etched editor group on the right, with New/Edit/Duplicate/Delete and
    /// Apply to Adapter.
    /// </summary>
    public sealed class ProfileForm : Win95Form
    {
        private ClassicListBox _list;
        private TextBox _name;
        private TextBox _ip;
        private TextBox _mask;
        private TextBox _prefix;
        private TextBox _gateway;
        private TextBox _dns1;
        private TextBox _dns2;
        private TextBox _vlan;
        private TextBox _description;
        private ClassicCheckBox _dhcp;
        private ClassicCheckBox _autoDns;
        private Label _hint;
        private ClassicButton _saveButton;

        private int _editingIndex = -1;
        private bool _loading;
        private bool _dirty;

        /// <summary>
        /// Creates the profile manager. It takes no adapter on purpose: this window
        /// never reads from or writes to an adapter, it only edits the stored list.
        /// </summary>
        public ProfileForm()
        {
            BuildChrome("Network Profiles");
            CloseOnEscape = true;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            Width = 740;
            Height = 424;

            BuildUi();
        }

        public event EventHandler ProfilesChanged;

        private void BuildUi()
        {
            Panel root = new Panel();
            root.Dock = DockStyle.Fill;
            root.BackColor = Win95Style.Face;
            root.Padding = new Padding(10, 10, 10, 10);
            ClientSurface.Controls.Add(root);

            // ---- Profiles list -----------------------------------------------
            ClassicGroupBox listGroup = new ClassicGroupBox();
            listGroup.Text = "Profiles";
            listGroup.SetBounds(10, 8, 210, 300);
            root.Controls.Add(listGroup);

            _list = new ClassicListBox();
            _list.SetBounds(10, 24, 190, 264);
            _list.SelectedIndexChanged += delegate { OnListSelectionChanged(); };
            listGroup.Controls.Add(_list);

            // ---- Editor -------------------------------------------------------
            ClassicGroupBox editor = new ClassicGroupBox();
            editor.Text = "Profile Configuration";
            editor.SetBounds(230, 8, 490, 300);
            root.Controls.Add(editor);
            BuildEditor(editor);

            // ---- List buttons -------------------------------------------------
            Panel listButtons = new Panel();
            listButtons.SetBounds(10, 314, 210, 26);
            listButtons.BackColor = Win95Style.Face;
            root.Controls.Add(listButtons);

            // Widths are sized to the caption so "Duplicate" is never clipped.
            AddButton(listButtons, "&New", 42, 0, delegate { NewProfile(); });
            AddButton(listButtons, "&Edit", 42, 46, delegate { LoadIntoEditor(); });
            AddButton(listButtons, "Dupli&cate", 62, 92, delegate { DuplicateProfile(); });
            AddButton(listButtons, "&Delete", 52, 158, delegate { DeleteProfile(); });

            // ---- Bottom actions -----------------------------------------------
            // This window only creates and manages profiles. Applying a profile to an
            // adapter is done from the main window (Apply Profile), so there is
            // deliberately no "Apply to Adapter" or "Capture from Adapter" here.
            _saveButton = new ClassicButton();
            _saveButton.Text = "&Save";
            _saveButton.SetBounds(230, 314, 88, 23);
            _saveButton.Click += delegate { SaveProfile(); };
            root.Controls.Add(_saveButton);

            ClassicButton export = new ClassicButton();
            export.Text = "&Export...";
            export.SetBounds(324, 314, 100, 23);
            export.Click += delegate { ExportProfiles(); };
            root.Controls.Add(export);

            ClassicButton import = new ClassicButton();
            import.Text = "&Import...";
            import.SetBounds(430, 314, 100, 23);
            import.Click += delegate { ImportProfiles(); };
            root.Controls.Add(import);

            _hint = new Label();
            _hint.Font = Win95Style.CreateFontSafe();
            _hint.BackColor = Win95Style.Face;
            _hint.AutoSize = false;
            _hint.SetBounds(230, 346, 490, 32);
            _hint.Text = HintText();
            root.Controls.Add(_hint);

            TrackEditorChanges();
            RefreshList();
            UpdateSaveState();
        }

        /// <summary>
        /// Watches every editor field so the Save button can reflect unsaved edits
        /// and closing the window can warn about losing them.
        /// </summary>
        private void TrackEditorChanges()
        {
            TextBox[] boxes = { _name, _ip, _mask, _prefix, _gateway, _dns1, _dns2, _vlan, _description };
            foreach (TextBox b in boxes)
            {
                if (b == null) continue;
                b.TextChanged += delegate { MarkDirty(); };
            }

            _dhcp.CheckedChanged += delegate { MarkDirty(); };
            _autoDns.CheckedChanged += delegate { MarkDirty(); };
        }

        private void MarkDirty()
        {
            if (_loading) return;
            _dirty = true;
            UpdateSaveState();
        }

        private void UpdateSaveState()
        {
            if (_saveButton == null) return;

            bool canSave = _dirty || (_editingIndex < 0 && _name != null && _name.Text.Trim().Length > 0);
            _saveButton.Enabled = canSave;

            if (_hint != null) _hint.Text = HintText();
        }

        private string HintText()
        {
            return "This window only creates and manages profiles.\n" +
                   "To put a profile on an adapter, use Apply Profile in the main window.";
        }
        private void AddButton(Panel parent, string caption, int width, int x, EventHandler onClick)
        {
            ClassicButton b = new ClassicButton();
            b.Text = caption;
            b.Width = width;
            b.Height = 23;
            b.SetBounds(x, 1, width, 23);
            b.Click += onClick;
            parent.Controls.Add(b);
        }

        private Label AddField(Control parent, string caption, int y, out SunkenPanel field)
        {
            Label l = new Label();
            l.Text = caption;
            l.Font = Win95Style.CreateFontSafe();
            l.BackColor = Win95Style.Face;
            l.AutoSize = false;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.SetBounds(10, y + 3, 96, 18);
            parent.Controls.Add(l);

            field = new SunkenPanel();
            field.SetBounds(110, y, 180, 21);
            parent.Controls.Add(field);
            return l;
        }

        private TextBox AddInput(SunkenPanel field, string name)
        {
            TextBox t = new TextBox();
            t.BorderStyle = BorderStyle.None;
            t.Font = Win95Style.CreateFontSafe();
            t.SetBounds(4, 3, 168, 15);
            t.Name = name;
            field.Controls.Add(t);
            return t;
        }

        private void BuildEditor(Control parent)
        {
            SunkenPanel f;

            AddField(parent, "Name:", 24, out f); _name = AddInput(f, "Name");
            AddField(parent, "IP Address:", 50, out f); _ip = AddInput(f, "Ip");
            AddField(parent, "Subnet Mask:", 76, out f); _mask = AddInput(f, "Mask");
            AddField(parent, "Prefix Length:", 102, out f); _prefix = AddInput(f, "Prefix");
            AddField(parent, "Gateway:", 128, out f); _gateway = AddInput(f, "Gateway");
            AddField(parent, "Primary DNS:", 160, out f); _dns1 = AddInput(f, "Dns1");
            AddField(parent, "Secondary DNS:", 186, out f); _dns2 = AddInput(f, "Dns2");
            AddField(parent, "VLAN ID:", 212, out f); _vlan = AddInput(f, "Vlan");

            _vlan.MaxLength = 4;

            // IP Configuration: static vs DHCP
            _dhcp = new ClassicCheckBox();
            _dhcp.Text = "Use DHCP";
            _dhcp.SetBounds(300, 52, 175, 16);
            _dhcp.CheckedChanged += delegate { UpdateFieldStates(); };
            parent.Controls.Add(_dhcp);
            Label ipLbl = new Label();
            ipLbl.Text = "IP Configuration:";
            ipLbl.Font = Win95Style.CreateFontSafe();
            ipLbl.BackColor = Win95Style.Face;
            ipLbl.AutoSize = false;
            ipLbl.SetBounds(300, 52, 90, 16);
            ipLbl.Visible = false;
            parent.Controls.Add(ipLbl);

            // DNS Configuration: static vs automatic
            _autoDns = new ClassicCheckBox();
            _autoDns.Text = "Automatic DNS";
            _autoDns.SetBounds(300, 164, 175, 16);
            _autoDns.CheckedChanged += delegate { UpdateFieldStates(); };
            parent.Controls.Add(_autoDns);

            Label desc = new Label();
            desc.Text = "Description:";
            desc.Font = Win95Style.CreateFontSafe();
            desc.BackColor = Win95Style.Face;
            desc.AutoSize = false;
            desc.TextAlign = ContentAlignment.TopLeft;
            desc.SetBounds(10, 244, 96, 18);
            parent.Controls.Add(desc);

            SunkenPanel descField = new SunkenPanel();
            descField.SetBounds(10, 262, 470, 30);
            parent.Controls.Add(descField);

            _description = new TextBox();
            _description.Multiline = true;
            _description.BorderStyle = BorderStyle.None;
            _description.Font = Win95Style.CreateFontSafe();
            _description.SetBounds(4, 3, 458, 24);
            descField.Controls.Add(_description);

            UpdateFieldStates();
        }

        /// <summary>Greys out the fields that do not apply to the chosen modes.</summary>
        private void UpdateFieldStates()
        {
            if (_loading) return;

            bool staticIp = !_dhcp.Checked;
            _ip.Enabled = staticIp;
            _mask.Enabled = staticIp;
            _prefix.Enabled = staticIp;
            _gateway.Enabled = true;

            bool staticDns = !_autoDns.Checked;
            _dns1.Enabled = staticDns;
            _dns2.Enabled = staticDns;
        }

        private void RefreshList()
        {
            int previous = _list.SelectedIndex;
            _loading = true;

            List<AdapterListItem> rows = new List<AdapterListItem>();
            foreach (NetworkProfile p in ProfileStore.Profiles)
                rows.Add(new AdapterListItem(null, p.Name, Describe(p)));

            _list.SetItems(rows);

            if (rows.Count > 0)
                _list.SelectedIndex = previous >= 0 && previous < rows.Count ? previous : 0;

            _loading = false;

            if (_list.SelectedIndex >= 0) LoadIntoEditor();
        }

        private static string Describe(NetworkProfile p)
        {
            string ip = p.IpConfig == IpMode.Dhcp ? "DHCP" : p.IpAddress;
            string vlan = p.HasVlan ? ("VLAN " + p.VlanValue) : "no VLAN";
            return ip + "   " + vlan;
        }

        private void OnListSelectionChanged()
        {
            if (_loading) return;
            LoadIntoEditor();
        }

        private void LoadIntoEditor()
        {
            int index = _list.SelectedIndex;
            if (index < 0 || index >= ProfileStore.Profiles.Count) return;

            _editingIndex = index;
            LoadIntoEditor(ProfileStore.Profiles[index]);
        }

        /// <summary>Fills the editor from a profile, without changing the selection.</summary>
        private void LoadIntoEditor(NetworkProfile p)
        {
            if (p == null) return;

            _loading = true;
            _name.Text = p.Name;
            _ip.Text = p.IpAddress;
            _mask.Text = p.SubnetMask;
            _prefix.Text = p.PrefixLength;
            _gateway.Text = p.Gateway;
            _dns1.Text = p.PrimaryDns;
            _dns2.Text = p.SecondaryDns;
            _vlan.Text = p.VlanId;
            _description.Text = p.Description;
            _dhcp.Checked = (p.IpConfig == IpMode.Dhcp);
            _autoDns.Checked = (p.DnsConfig == DnsMode.Automatic);
            _loading = false;

            _dirty = false;
            UpdateFieldStates();
            UpdateSaveState();
        }

        private void NewProfile()
        {
            _editingIndex = -1;
            _loading = true;
            _name.Text = "New Profile";
            _ip.Text = "";
            _mask.Text = "255.255.255.0";
            _prefix.Text = "24";
            _gateway.Text = "";
            _dns1.Text = "";
            _dns2.Text = "";
            _vlan.Text = "";
            _description.Text = "";
            _dhcp.Checked = false;
            _autoDns.Checked = false;
            _loading = false;
            _list.SelectedIndex = -1;
            UpdateFieldStates();
            _dirty = false;
            UpdateSaveState();
            _name.Focus();
            _name.SelectAll();
        }

        private void DuplicateProfile()
        {
            int index = _list.SelectedIndex;
            if (index < 0 || index >= ProfileStore.Profiles.Count) return;

            NetworkProfile copy = ProfileStore.Profiles[index].Clone();
            copy.Name = ProfileStore.UniqueName(copy.Name + " copy");

            ProfileStore.Add(copy);
            string error;
            ProfileStore.Save(out error);
            RefreshList();
            _list.SelectedIndex = ProfileStore.Profiles.Count - 1;
            RaiseChanged();
        }

        private void DeleteProfile()
        {
            int index = _list.SelectedIndex;
            if (index < 0 || index >= ProfileStore.Profiles.Count) return;

            string name = ProfileStore.Profiles[index].Name;
            if (!Win95MessageBox.Ask(this, "Network Profiles",
                    "Delete profile \"" + name + "\"?",
                    "This cannot be undone."))
            {
                return;
            }

            ProfileStore.RemoveAt(index);
            string error;
            ProfileStore.Save(out error);
            RefreshList();
            RaiseChanged();
        }
        /// <summary>Builds a profile from the editor fields without touching the store.</summary>
        private NetworkProfile BuildFromEditor(string name)
        {
            NetworkProfile p = new NetworkProfile();
            p.Name = name;
            p.IpAddress = _ip.Text.Trim();
            p.SubnetMask = _mask.Text.Trim();
            p.PrefixLength = _prefix.Text.Trim();
            p.Gateway = _gateway.Text.Trim();
            p.PrimaryDns = _dns1.Text.Trim();
            p.SecondaryDns = _dns2.Text.Trim();
            p.VlanId = _vlan.Text.Trim();
            p.Description = _description.Text.Trim();
            p.IpConfig = _dhcp.Checked ? IpMode.Dhcp : IpMode.Static;
            p.DnsConfig = _autoDns.Checked ? DnsMode.Automatic : DnsMode.Static;
            return p;
        }

        /// <summary>
        /// Validates the editor and writes it to profiles.json. Never changes the
        /// adapter. Returns the stored profile, or null when nothing was stored.
        ///
        /// This is the single write path, so New -> fill in -> Save and
        /// Apply to Adapter behave identically and cannot drift apart.
        /// </summary>
        private NetworkProfile CommitEditor()
        {
            string name = _name.Text.Trim();

            if (name.Length == 0)
            {
                Win95MessageBox.ShowWarning(this, "Network Profiles",
                    _list.SelectedIndex < 0 ? "No profile is selected." : "Enter a profile name.",
                    _list.SelectedIndex < 0
                        ? "Select a profile from the list, or press New to create one."
                        : "The name cannot be empty.");
                return null;
            }

            int existing = ProfileStore.IndexOf(name);
            int target = _editingIndex;

            if (target < 0 && existing >= 0)
            {
                bool overwrite = Win95MessageBox.Ask(this, "Network Profiles",
                    "A profile named \"" + name + "\" already exists.",
                    "Yes - overwrite the existing profile\nNo - save it as a new profile instead");

                if (overwrite)
                {
                    target = existing;
                }
                else
                {
                    name = ProfileStore.UniqueName(name);
                    _name.Text = name;
                }
            }

            NetworkProfile candidate = BuildFromEditor(name);

            string problem = candidate.Validate();
            if (problem != null)
            {
                Win95MessageBox.ShowError(this, "Network Profiles",
                    "The profile was not saved because it is not valid.", problem);
                return null;
            }

            if (target >= 0 && target < ProfileStore.Profiles.Count)
                ProfileStore.Profiles[target] = candidate;
            else
                ProfileStore.Profiles.Add(candidate);

            _editingIndex = target >= 0 && target < ProfileStore.Profiles.Count
                ? target
                : ProfileStore.Profiles.Count - 1;

            string error;
            if (!ProfileStore.Save(out error))
            {
                Win95MessageBox.ShowError(this, "Network Profiles",
                    "The profile could not be written to disk.", error);
                return null;
            }

            _loading = true;
            _dirty = false;
            _loading = false;

            RefreshList();

            if (_editingIndex >= 0 && _editingIndex < ProfileStore.Profiles.Count)
            {
                _list.SelectedIndex = _editingIndex;
                LoadIntoEditor();
            }

            UpdateSaveState();
            RaiseChanged();
            return candidate;
        }

        /// <summary>Save button: stores the profile and leaves the adapter alone.</summary>
        private void SaveProfile()
        {
            NetworkProfile saved = CommitEditor();
            if (saved == null) return;

            Win95MessageBox.ShowInfo(this, "Network Profiles",
                "Saved profile \"" + saved.Name + "\".",
                DescribeValues(saved) + "\n\nThe adapter was not changed.");
        }

        /// <summary>Multi-line summary of a profile's values.</summary>
        private static string DescribeValues(NetworkProfile p)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();

            sb.AppendLine("IP configuration:  " +
                (p.IpConfig == IpMode.Dhcp
                    ? "DHCP"
                    : p.IpAddress + "  mask " + p.SubnetMask +
                      (string.IsNullOrEmpty(p.PrefixLength) ? "" : "  prefix " + p.PrefixLength)));

            sb.AppendLine("Gateway:  " + (string.IsNullOrEmpty(p.Gateway) ? "(none)" : p.Gateway));

            sb.Append("DNS:  " + (p.DnsConfig == DnsMode.Automatic
                ? "Automatic (from DHCP)"
                : (string.IsNullOrEmpty(p.PrimaryDns) ? "(none)" : p.PrimaryDns)));

            if (p.DnsConfig == DnsMode.Static && !string.IsNullOrEmpty(p.SecondaryDns))
                sb.Append(", " + p.SecondaryDns);

            sb.AppendLine();
            sb.Append("VLAN:  " + (p.HasVlan ? p.VlanValue.ToString() : "none (untagged)"));

            return sb.ToString();
        }

        private void ExportProfiles()
        {
            if (ProfileStore.Profiles.Count == 0)
            {
                Win95MessageBox.ShowInfo(this, "Network Profiles",
                    "There are no profiles to export.", "Create a profile first.");
                return;
            }

            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "Export Network Profiles";
                dlg.FileName = "vlan-profiles.json";
                dlg.Filter = "Profile files (*.json)|*.json|All files (*.*)|*.*";
                dlg.DefaultExt = "json";

                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;

                try
                {
                    System.IO.File.WriteAllText(dlg.FileName, ProfileStore.Serialize());
                    Win95MessageBox.ShowInfo(this, "Network Profiles",
                        "Profiles exported.", dlg.FileName);
                }
                catch (Exception ex)
                {
                    Win95MessageBox.ShowError(this, "Network Profiles",
                        "The profiles could not be exported.", ex.Message);
                }
            }
        }

        private void ImportProfiles()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Import Network Profiles";
                dlg.Filter = "Profile files (*.json)|*.json|All files (*.*)|*.*";

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    string json = System.IO.File.ReadAllText(dlg.FileName);
                    string error;
                    List<string> rejected;
                    int added = ProfileStore.Import(json, out error, out rejected);

                    if (added > 0)
                    {
                        string saveError;
                        ProfileStore.Save(out saveError);
                        RefreshList();
                        RaiseChanged();
                    }

                    System.Text.StringBuilder detail = new System.Text.StringBuilder();
                    if (rejected.Count > 0)
                    {
                        detail.AppendLine("Rejected entries:");
                        foreach (string r in rejected) detail.AppendLine("  " + r);
                    }

                    Win95MessageBox.ShowInfo(this, "Network Profiles",
                        added > 0 ? ("Imported " + added + " profile(s).") : "No profiles were imported.",
                        error != null ? (error + "\n\n" + detail) : detail.ToString());
                }
                catch (Exception ex)
                {
                    Win95MessageBox.ShowError(this, "Network Profiles",
                        "The profiles could not be imported.", ex.Message);
                }
            }
        }

        private void RaiseChanged()
        {
            EventHandler h = ProfilesChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override void RequestClose()
        {
            // Now that Save is an explicit button, unsaved edits are offered
            // explicitly rather than being written (or silently discarded) on close.
            if (_dirty)
            {
                string question = _editingIndex >= 0
                    ? "Save the changes to \"" + _name.Text.Trim() + "\" before closing?"
                    : "Save \"" + _name.Text.Trim() + "\" as a new profile before closing?";

                bool save = Win95MessageBox.Ask(this, "Network Profiles",
                    question,
                    "Yes - save the profile\nNo - discard the changes");

                if (save)
                {
                    NetworkProfile saved = CommitEditor();
                    if (saved == null)
                    {
                        // Invalid or unnamed: refuse to close so nothing is lost.
                        return;
                    }
                }
            }

            base.RequestClose();
        }
    }
}
