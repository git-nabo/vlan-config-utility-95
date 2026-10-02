using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using VlanConfig95.Core;

namespace VlanConfig95.Ui
{
    /// <summary>
    /// The main VLAN Configuration window, styled as a Windows 95 Control Panel
    /// applet: adapter list on the left, properties on the right, VLAN group below,
    /// and a classic status bar.
    /// </summary>
    public sealed class MainForm : Win95Form
    {
        private readonly NetworkService _service = new NetworkService();
        private bool _profilesLoaded;

        private ClassicListBox _adapterList;
        private ClassicGroupBox _infoGroup;
        private ClassicGroupBox _vlanGroup;
        private Win95StatusBar _statusBar;
        private ClassicCheckBox _showDisconnected;
        private ClassicCheckBox _showAllInterfaces;
        private TextBox _vlanInput;
        private ClassicButton _applyButton;
        private ClassicButton _resetButton;
        private ClassicButton _refreshButton;
        private ClassicButton _detailsButton;
        private MenuStrip _menu;
        private ToolStripMenuItem _profilesMenuItem;
        private ToolStripMenuItem _defaultsMenuItem;
        private ToolStripMenuItem _loggingMenuItem;
        private ComboBox _profileCombo;
        private ClassicButton _applyProfileButton;
        private ClassicButton _restoreButton;
        private ClassicButton _saveProfileButton;
        private Label _activeProfileLabel;
        private ProfileApplier _applier;
        private bool _suppressProfileEvents;
        private ToolStripMenuItem _detailsMenuItem;

        private readonly List<AdapterInfo> _adapters = new List<AdapterInfo>();
        private AdapterInfo _selected;
        private VlanSupport _support;

        private bool _busy;
        private bool _handlingSelection;
        private bool _suppressFilters;
        private readonly Dictionary<int, Inspection> _supportCache = new Dictionary<int, Inspection>();

        public MainForm()
        {
            BuildChrome("VLAN Configuration Utility");
            AllowMaximize = true;

            // Height accounts for: caption (20) + menu bar (19) + status bar (20) plus
            // the root panel padding. The profile group is the lowest control and ends
            // at y=548, so the client area needs 556px of content and 64px of chrome.
            Width = 660;
            Height = 628;
            MinimumSize = new Size(660, 628);

            BuildMenu();
            BuildLayout();

            Shown += delegate
            {
                // Startup and the Refresh button share this one pipeline, so a refresh
                // rebuilds exactly the same master cache the initial load produces.
                ReloadEverything(true);
                EnsureProfilesLoaded();
                UpdatePrivilegeDisplay();
            };
        }

        /// <summary>
        /// The single loading pipeline used by both application startup and the
        /// Refresh button/menu item.
        ///
        /// It re-enumerates the adapters, discards the previous property cache and
        /// then re-inspects every adapter in the background, so afterwards selecting an
        /// adapter is served from the cache and never triggers a fresh PowerShell read.
        /// Filter state and the selected adapter survive because RefreshAdapters only
        /// reads the checkboxes and restores the selection by interface index.
        /// </summary>
        private void ReloadEverything(bool selectFirst)
        {
            RefreshAdapters(selectFirst);
            StartSupportPreload();
        }

        private int _preloadCursor;
        private int _preloadDone;
        private int _preloadTotal;
        private bool _preloadRunning;
        private int _preloadGeneration;
        private readonly object _preloadLock = new object();

        /// <summary>
        /// Inspects every adapter in the background, filling the support cache. Runs on
        /// a small pool of worker threads and never touches the UI thread, so the window
        /// stays usable (and clickable) while it works.
        /// </summary>
        private void StartSupportPreload()
        {
            int generation;

            lock (_preloadLock)
            {
                if (_preloadRunning) return;

                List<AdapterInfo> targets = new List<AdapterInfo>();
                foreach (AdapterInfo a in _adapters)
                {
                    if (!HasCachedInspection(a.InterfaceIndex)) targets.Add(a);
                }

                if (targets.Count == 0) return;

                _preloadRunning = true;
                _preloadCursor = 0;
                _preloadDone = 0;
                _preloadTotal = targets.Count;
                generation = _preloadGeneration;

                int workers = Math.Min(6, targets.Count);
                for (int i = 0; i < workers; i++)
                {
                    int captured = generation;
                    ThreadPool.QueueUserWorkItem(delegate { PreloadWorker(targets, captured); });
                }
            }
        }

        private void PreloadWorker(List<AdapterInfo> targets, int generation)
        {
            while (true)
            {
                AdapterInfo next = null;

                lock (_preloadLock)
                {
                    // A refresh started while this worker was busy: its results would
                    // be stale, so stop instead of overwriting the new cache.
                    if (generation != _preloadGeneration) return;
                    if (_preloadCursor >= targets.Count) break;
                    next = targets[_preloadCursor++];
                }

                if (next == null) continue;

                string error;
                VlanSupport support = null;

                try
                {
                    support = _service.InspectVlan(next, out error);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                bool finished;
                int done;

                lock (_preloadLock)
                {
                    if (generation != _preloadGeneration) return;

                    CacheSupport(next.InterfaceIndex, support, error);
                    done = ++_preloadDone;
                    finished = (_preloadDone >= _preloadTotal);
                    if (finished) _preloadRunning = false;
                }

                ReportPreloadProgress(done, finished);
            }
        }

        /// <summary>
        /// Drops the whole property cache and invalidates any preload still running, so
        /// the next StartSupportPreload rebuilds it from scratch.
        /// </summary>
        private void ResetInspectionCache()
        {
            lock (_preloadLock)
            {
                _supportCache.Clear();
                _preloadGeneration++;
                _preloadRunning = false;
                _preloadDone = 0;
                _preloadTotal = 0;
                _preloadCursor = 0;
            }
        }

        private bool HasCachedInspection(int interfaceIndex)
        {
            lock (_preloadLock) return _supportCache.ContainsKey(interfaceIndex);
        }

        private bool TryGetCachedInspection(int interfaceIndex, out Inspection entry)
        {
            lock (_preloadLock) return _supportCache.TryGetValue(interfaceIndex, out entry);
        }

        private void RemoveCachedInspection(int interfaceIndex)
        {
            lock (_preloadLock) _supportCache.Remove(interfaceIndex);
        }

        private void ReportPreloadProgress(int done, bool finished)
        {
            try
            {
                if (IsDisposed) return;

                if (InvokeRequired)
                {
                    BeginInvoke(new MethodInvoker(delegate { ReportPreloadProgress(done, finished); }));
                    return;
                }

                SetStatus(finished
                    ? "Adapter properties loaded (" + done + " adapters)."
                    : "Loading adapter properties... (" + done + "/" + _preloadTotal + ")",
                    Elevation.StatusText());
            }
            catch
            {
                // Cosmetic only - never let progress reporting break a load.
            }
        }

        private void BuildMenu()
        {
            _menu = new MenuStrip();

            ToolStripMenuItem file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add("&Refresh Adapters", null, delegate { ReloadEverything(false); });
            file.DropDownItems.Add("&Options...", null, delegate { ShowOptions(); });
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add("E&xit", null, delegate { Close(); });

            _detailsMenuItem = new ToolStripMenuItem("&Details");
            _detailsMenuItem.Enabled = false;
            _detailsMenuItem.Click += delegate { ShowDetails(); };

            _profilesMenuItem = new ToolStripMenuItem("&Profiles");
            _profilesMenuItem.Click += delegate { ShowProfiles(); };

            // Defaults: the persistent options, promoted to the top level because they
            // change how the utility behaves for every adapter, not just this session.
            _defaultsMenuItem = new ToolStripMenuItem("&Defaults");
            _defaultsMenuItem.DropDownItems.Add("&Logging...", null, delegate { ShowOptions(); });
            _defaultsMenuItem.DropDownItems.Add(new ToolStripSeparator());
            _defaultsMenuItem.DropDownItems.Add("&Restore Factory Settings", null,
                delegate { RestoreDefaults(); });

            _loggingMenuItem = new ToolStripMenuItem("&Logging");
            _loggingMenuItem.Checked = Log.Enabled;
            _loggingMenuItem.Click += delegate { ToggleLogging(); };
            _defaultsMenuItem.DropDownItems.Add(_loggingMenuItem);


            ToolStripMenuItem help = new ToolStripMenuItem("&Help");
            help.DropDownItems.Add("&About VLAN Configuration Utility", null, delegate { ShowAbout(); });

            _menu.Items.AddRange(new ToolStripItem[] { file, _defaultsMenuItem, _profilesMenuItem, _detailsMenuItem, help });
        }

        private void SetStatus(string primary, string secondary)
        {
            if (_statusBar != null)
                _statusBar.SetPanes(primary, secondary);
        }
private void BuildLayout()
        {
            Panel root = new Panel();
            root.Dock = DockStyle.Fill;
            root.BackColor = Win95Style.Face;
            root.Padding = new Padding(8, 10, 8, 8);
            ClientSurface.Controls.Add(root);

            _infoGroup = new ClassicGroupBox();
            _infoGroup.Text = "Adapter Information";
            _infoGroup.SetBounds(12, 10, 388, 204);
            root.Controls.Add(_infoGroup);

            BuildInfoFields(_infoGroup);

            _adapterList = new ClassicListBox();
            _adapterList.SetBounds(12, 222, 388, 140);
            _adapterList.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _adapterList.SelectedIndexChanged += delegate { OnAdapterSelected(); };
            root.Controls.Add(_adapterList);

            _showDisconnected = new ClassicCheckBox();
            _showDisconnected.Text = "Show disconnected adapters";
            _showDisconnected.SetBounds(14, 366, 190, 16);
            _showDisconnected.Checked = true;
            _showDisconnected.CheckedChanged += delegate { ApplyFilters(); };
            root.Controls.Add(_showDisconnected);

            _showAllInterfaces = new ClassicCheckBox();
            _showAllInterfaces.Text = "Show all interfaces (incl. virtual)";
            _showAllInterfaces.SetBounds(210, 366, 200, 16);
            _showAllInterfaces.Checked = true;
            // Filtering is a local view over the cached adapter list. It must never
            // trigger a re-enumeration: that was re-running the slow PowerShell scan
            // and left the checkbox appearing to reset itself.
            _showAllInterfaces.CheckedChanged += delegate { ApplyFilters(); };
            root.Controls.Add(_showAllInterfaces);

            _vlanGroup = new ClassicGroupBox();
            _vlanGroup.Text = "VLAN Configuration";
            _vlanGroup.SetBounds(12, 388, 388, 74);
            root.Controls.Add(_vlanGroup);
            BuildVlanGroup(_vlanGroup);

            _detailsButton = new ClassicButton();
            _detailsButton.Text = "Details";
            _detailsButton.SetBounds(408, 10, 90, 23);
            _detailsButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _detailsButton.Enabled = false;
            _detailsButton.Click += delegate { ShowDetails(); };
            root.Controls.Add(_detailsButton);
            BuildSidePanel(root);

            BuildProfilePanel(root);

            _statusBar = new Win95StatusBar();
            _statusBar.Dock = DockStyle.Bottom;
            _statusBar.Height = 20;
            _statusBar.SetPanes("Ready.", Elevation.StatusText());
            ClientSurface.Controls.Add(_statusBar);

            // The menu bar is added last on purpose. WinForms lays out docked
            // children in reverse collection order, so the fill panel (added first)
            // receives whatever space the menu bar and status bar leave behind.
            BuildMenuBar(_menu);
        }

        private void BuildSidePanel(Panel root)
        {
            ClassicGroupBox privilegeGroup = new ClassicGroupBox();
            privilegeGroup.Text = "Privileges";
            privilegeGroup.SetBounds(408, 38, 208, 74);
            root.Controls.Add(privilegeGroup);

            Label elevationLabel = new Label();
            elevationLabel.Name = "ElevationLabel";
            elevationLabel.AutoSize = false;
            elevationLabel.BorderStyle = BorderStyle.None;
            elevationLabel.BackColor = Win95Style.Face;
            elevationLabel.ForeColor = Win95Style.WindowText;
            elevationLabel.Font = Win95Style.CreateFontSafe();
            elevationLabel.TextAlign = ContentAlignment.MiddleLeft;
            elevationLabel.Dock = DockStyle.Fill;
            privilegeGroup.Controls.Add(elevationLabel);

            ClassicButton elevateButton = new ClassicButton();
            elevateButton.Name = "ElevateButton";
            elevateButton.Text = "Run as Administrator";
            elevateButton.Dock = DockStyle.Bottom;
            elevateButton.Height = 23;
            elevateButton.Click += delegate { RequestElevation(); };
            privilegeGroup.Controls.Add(elevateButton);

            _applyButton = new ClassicButton();
            _applyButton.Text = "Apply VLAN";
            _applyButton.SetBounds(408, 120, 100, 23);
            _applyButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _applyButton.Enabled = false;
            _applyButton.Click += delegate { ApplyVlan(); };
            root.Controls.Add(_applyButton);

            _resetButton = new ClassicButton();
            _resetButton.Text = "Reset VLAN";
            _resetButton.SetBounds(516, 120, 100, 23);
            _resetButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _resetButton.Enabled = false;
            _resetButton.Click += delegate { ResetVlan(); };
            root.Controls.Add(_resetButton);

            _refreshButton = new ClassicButton();
            _refreshButton.Text = "Refresh";
            _refreshButton.SetBounds(408, 149, 100, 23);
            _refreshButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _refreshButton.Click += delegate { ReloadEverything(false); };
            root.Controls.Add(_refreshButton);

            ClassicButton closeButton = new ClassicButton();
            closeButton.Text = "Close";
            closeButton.SetBounds(516, 149, 100, 23);
            closeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            closeButton.IsDefaultButton = true;
            closeButton.Click += delegate { Close(); };
            root.Controls.Add(closeButton);

            Label logLabel = new Label();
            logLabel.Name = "LogLabel";
            logLabel.AutoSize = false;
            logLabel.BorderStyle = BorderStyle.None;
            logLabel.BackColor = Win95Style.Face;
            logLabel.ForeColor = Win95Style.WindowText;
            logLabel.Font = Win95Style.CreateFontSafe();
            logLabel.TextAlign = ContentAlignment.TopLeft;
            logLabel.SetBounds(410, 180, 204, 60);
            logLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            root.Controls.Add(logLabel);
        }

/// <summary>Builds the fixed label/value grid inside the Adapter Information group.</summary>
        private void BuildInfoFields(Control parent)
        {
            string[] labels =
            {
                "Name:", "Description:", "Interface Index:", "MAC Address:",
                "IPv4 Address:", "Link Status:", "Link Speed:", "Driver / Provider:",
                "Interface Type:"
            };

            for (int i = 0; i < labels.Length; i++)
            {
                int y = 12 + (i * 21);

                Label l = new Label();
                l.Text = labels[i];
                l.Font = Win95Style.CreateFontSafe();
                l.BackColor = Win95Style.Face;
                l.ForeColor = Win95Style.WindowText;
                l.AutoSize = false;
                l.TextAlign = ContentAlignment.MiddleLeft;
                l.SetBounds(12, y, 100, 18);
                l.Name = "InfoLabel" + i;
                parent.Controls.Add(l);

                TextBox v = new TextBox();
                v.ReadOnly = true;
                v.BorderStyle = BorderStyle.None;
                v.BackColor = Win95Style.Face;
                v.ForeColor = Win95Style.WindowText;
                v.Font = Win95Style.CreateFontSafe();
                v.SetBounds(116, y + 2, 258, 16);
                v.Name = "InfoValue" + i;
                v.TabStop = false;
                parent.Controls.Add(v);
            }
        }

        /// <summary>Builds the VLAN input row and action buttons.</summary>
        private void BuildVlanGroup(Control parent)
        {
            Label caption = new Label();
            caption.Text = "VLAN ID:";
            caption.Font = Win95Style.CreateFontSafe();
            caption.BackColor = Win95Style.Face;
            caption.AutoSize = false;
            caption.TextAlign = ContentAlignment.MiddleLeft;
            caption.SetBounds(12, 16, 58, 18);
            parent.Controls.Add(caption);

            SunkenPanel field = new SunkenPanel();
            field.SetBounds(74, 15, 78, 21);
            parent.Controls.Add(field);

            _vlanInput = new TextBox();
            _vlanInput.BorderStyle = BorderStyle.None;
            _vlanInput.Font = Win95Style.CreateFontSafe();
            _vlanInput.MaxLength = 4;
            _vlanInput.Text = string.Empty;
            _vlanInput.SetBounds(4, 3, 66, 15);
            _vlanInput.Name = "VlanInput";
            _vlanInput.Enabled = false;
            field.Controls.Add(_vlanInput);

            Label range = new Label();
            range.Text = "(1 - 4094)";
            range.Font = Win95Style.CreateFontSafe();
            range.BackColor = Win95Style.Face;
            range.AutoSize = false;
            range.TextAlign = ContentAlignment.MiddleLeft;
            range.SetBounds(160, 16, 90, 18);
            parent.Controls.Add(range);

            Label property = new Label();
            property.Name = "PropertyLabel";
            property.Font = Win95Style.CreateFontSafe();
            property.BackColor = Win95Style.Face;
            property.AutoSize = false;
            property.TextAlign = ContentAlignment.MiddleLeft;
            property.ForeColor = Win95Style.WindowText;
            property.SetBounds(12, 38, 364, 18);
            property.Text = "Property: (none detected)";
            parent.Controls.Add(property);
        }

        /// <summary>
        /// Re-enumerates adapters and repopulates the list. This is the ONLY routine
        /// that talks to the network for adapter discovery; the Refresh button is its
        /// only caller.
        ///
        /// The filter checkboxes are read but never written, and the selected adapter
        /// is restored by interface index when it still exists.
        /// </summary>
        private void RefreshAdapters(bool selectFirst)
        {
            if (_busy)
                return;

            _busy = true;

            // Keep the filters from reacting to the list rebuild below.
            _suppressFilters = true;

            try
            {
                SetStatus("Reading network adapters...", Elevation.StatusText());
                Refresh();

                string error;
                List<AdapterInfo> result = _service.EnumerateAdapters(out error);

                _adapters.Clear();
                _adapters.AddRange(result);

                // A full refresh re-reads the drivers, so every cached inspection is
                // dropped here and rebuilt from scratch by the preload in
                // ReloadEverything, exactly as at startup.
                ResetInspectionCache();

                if (error != null)
                {
                    _adapterList.Clear();
                    _selected = null;
                    ClearInfoFields();
                    SetStatus("Enumeration failed.", "Press Details for more information.");
                    Win95MessageBox.ShowError(this, "Network Adapters", error,
                        _service.LastRawDetails);
                    return;
                }

                if (selectFirst) _selected = null;

                PopulateAdapterList();

                if (_adapterList.Items.Count == 0)
                {
                    SetStatus("No adapters matched the current filters.", Elevation.StatusText());
                }
                else
                {
                    SetStatus(_adapters.Count + " network adapter(s) found, " +
                              _adapterList.Items.Count + " shown.", Elevation.StatusText());
                }

                // Surface the freshly selected adapter without blocking on discovery.
                if (_selected != null) OnAdapterSelected();
            }
            finally
            {
                _suppressFilters = false;
                _busy = false;
            }
        }

        /// <summary>
        /// Re-applies the two filter checkboxes to the already-loaded adapter list.
        ///
        /// This is the single entry point for filtering. It never touches the network
        /// and never writes back to either checkbox, so the user's choice is the
        /// authoritative state and survives every subsequent repaint, refresh and
        /// selection change.
        /// </summary>
        private void ApplyFilters()
        {
            if (_suppressFilters) return;

            PopulateAdapterList();

            int shown = _adapterList.Items.Count;
            SetStatus(shown + " of " + _adapters.Count + " adapter(s) shown.",
                Elevation.StatusText());
        }

        /// <summary>Rebuilds the visible list rows, honouring the filter checkboxes.</summary>
        private void PopulateAdapterList()
        {
            // Suppress the selection handler while the rows are rebuilt: it would
            // otherwise fire for each intermediate state and fight the selection we
            // are about to restore.
            bool previousGuard = _handlingSelection;
            _handlingSelection = true;

            try
            {
                int previousIndex = _adapterList.SelectedIndex;
                AdapterInfo previous = _selected;

                List<AdapterListItem> rows = new List<AdapterListItem>();

                foreach (AdapterInfo a in _adapters)
                {
                    if (!_showDisconnected.Checked && !a.IsConnected) continue;
                    if (!_showAllInterfaces.Checked && IsNonPhysical(a)) continue;

                    rows.Add(new AdapterListItem(a, a.Name, DescribeAdapter(a)));
                }

                _adapterList.SetItems(rows);

                if (rows.Count == 0)
                {
                    _selected = null;
                    _support = null;
                    ClearInfoFields();
                    return;
                }

                // Keep the same adapter selected when it is still visible. Only fall
                // back to the first row when the selected adapter was filtered out.
                int target = -1;

                if (previous != null)
                {
                    for (int i = 0; i < rows.Count; i++)
                    {
                        if (rows[i].Adapter.InterfaceIndex == previous.InterfaceIndex)
                        {
                            target = i;
                            break;
                        }
                    }
                }
                else if (previousIndex >= 0 && previousIndex < rows.Count)
                {
                    target = previousIndex;
                }

                if (target < 0) target = 0;

                _adapterList.SelectedIndex = target;
                EnsureListVisible(target);

                _selected = rows[target].Adapter;
            }
            finally
            {
                _handlingSelection = previousGuard;
            }
        }

        /// <summary>
        /// Re-reads the properties of the currently selected adapter only.
        ///
        /// Used after a VLAN change or a profile apply, where the selected adapter's
        /// data is known to be stale. Only that one adapter's cache entry is dropped,
        /// so this never re-enumerates all adapters and never disturbs the filters,
        /// the checkbox state or the selection.
        /// </summary>
        private void RefreshSelectedAdapter()
        {
            if (_selected == null)
                return;

            RemoveCachedInspection(_selected.InterfaceIndex);

            OnAdapterSelected();
        }

        private void EnsureListVisible(int index)
        {
            if (index < 0)
                return;

            int visible = Math.Max(1, (_adapterList.Height - 4) / _adapterList.RowHeight);
            int scroll = _adapterList.ScrollPosition;

            if (index < scroll)
                _adapterList.ScrollTo(index);
            else if (index >= scroll + visible)
                _adapterList.ScrollTo(index - visible + 1);
        }

        /// <summary>
        /// Handles a new adapter selection: fills details and detects VLAN support.
        ///
        /// Two guards matter here. <see cref="_handlingSelection"/> stops the
        /// SelectedIndexChanged event we raise ourselves from re-entering this method,
        /// and the support cache means clicking an adapter that was already inspected
        /// does not spawn another slow PowerShell query.
        /// </summary>
        private void OnAdapterSelected()
        {
            if (_handlingSelection) return;
            _handlingSelection = true;

            try
            {
                AdapterListItem item = _adapterList.SelectedItem;

                _selected = (item != null) ? item.Adapter : null;
                _support = null;

                if (_selected == null)
                {
                    ClearInfoFields();
                    return;
                }

                FillInfoFields(_selected);

                Inspection cached;
                if (TryGetCachedInspection(_selected.InterfaceIndex, out cached))
                {
                    // Already inspected: show the result immediately, no PowerShell.
                    _support = cached.Support;
                    SetStatus("Adapter properties (cached).", Elevation.StatusText());
                    UpdateProfileButtons();
                    UpdateVlanControls(cached.Error);
                    return;
                }

                SetStatus("Reading adapter properties...", Elevation.StatusText());
                Refresh();

                string error;
                _support = _service.InspectVlan(_selected, out error);
                CacheSupport(_selected.InterfaceIndex, _support, error);

                UpdateProfileButtons();
                UpdateVlanControls(error);
            }
            finally
            {
                _handlingSelection = false;
            }
        }

        private void CacheSupport(int interfaceIndex, VlanSupport support, string error)
        {
            Inspection entry = new Inspection();
            entry.Support = support;
            entry.Error = error;

            // Always called from inside _preloadLock, so the dictionary is only ever
            // mutated under that lock.
            _supportCache[interfaceIndex] = entry;
        }

        /// <summary>A cached VLAN inspection: the result plus its error text.</summary>
        private sealed class Inspection
        {
            public VlanSupport Support;
            public string Error;
        }

        private void FillInfoFields(AdapterInfo a)
        {
            SetInfo(0, a.Name);
            SetInfo(1, a.Description);
            SetInfo(2, a.InterfaceIndex.ToString(CultureInfo.InvariantCulture));
            SetInfo(3, string.IsNullOrEmpty(a.MacAddress) ? "(not available)" : a.MacAddress);
            SetInfo(4, string.IsNullOrEmpty(a.IPv4Addresses) ? "(not configured)" : a.IPv4Addresses);
            SetInfo(5, string.IsNullOrEmpty(a.Status) ? "Unknown" : a.Status);
            SetInfo(6, string.IsNullOrEmpty(a.LinkSpeed) ? "-" : a.LinkSpeed);
            SetInfo(7, BuildDriverText(a));
            SetInfo(8, a.CategoryLabel + (string.IsNullOrEmpty(a.MediaType) ? "" : " (" + a.MediaType + ")"));
        }

        private static string BuildDriverText(AdapterInfo a)
        {
            string provider = string.IsNullOrEmpty(a.DriverProvider) ? "Unknown" : a.DriverProvider;
            string version = string.IsNullOrEmpty(a.DriverVersion) ? "" : " " + a.DriverVersion;
            return provider + version;
        }

        private void SetInfo(int index, string value)
        {
            Control[] controls = _infoGroup.Controls.Find("InfoValue" + index, true);
            if (controls.Length > 0)
                controls[0].Text = value;
        }

        private void ClearInfoFields()
        {
            for (int i = 0; i < 9; i++)
                SetInfo(i, string.Empty);

            SetPropertyText("Property: (none detected)");
            _vlanInput.Text = string.Empty;
            _vlanInput.Enabled = false;
            _applyButton.Enabled = false;
            _resetButton.Enabled = false;
            _detailsButton.Enabled = false;
            if (_detailsMenuItem != null)
                _detailsMenuItem.Enabled = false;
        }

        private void SetPropertyText(string text)
        {
            Control[] controls = _vlanGroup.Controls.Find("PropertyLabel", true);
            if (controls.Length > 0)
                controls[0].Text = text;
        }

        /// <summary>Enables or disables VLAN controls based on genuine detection.</summary>
        private void UpdateVlanControls(string inspectError)
        {
            bool elevated = Elevation.IsElevated();
            bool usable = _support != null && _support.Supported;

            _vlanInput.Enabled = usable;
            _applyButton.Enabled = usable;
            _resetButton.Enabled = usable && _support.CurrentVlanId.HasValue;

            bool hasDetails = _service.LastRawDetails.Length > 0;
            _detailsButton.Enabled = hasDetails;
            if (_detailsMenuItem != null)
                _detailsMenuItem.Enabled = hasDetails;

            if (inspectError != null)
            {
                SetPropertyText("Property: unavailable (" + inspectError + ")");
                SetStatus("Could not read adapter properties.", "Press Details for more information.");
                return;
            }

            if (!usable)
            {
                SetPropertyText("Property: none - VLAN configuration is not exposed by this driver.");
                SetStatus("Selected adapter does not expose VLAN configuration.",
                          "VLAN configuration is not exposed by this network adapter driver.");
                return;
            }

            VlanProperty p = _support.Property;
            string current = _support.CurrentVlanId.HasValue
                ? _support.CurrentVlanId.Value.ToString(CultureInfo.InvariantCulture)
                : "None (untagged)";

            SetPropertyText("Property: " + p.RegistryKeyword +
                            " (" + (string.IsNullOrEmpty(p.DisplayName) ? "no display name" : p.DisplayName) + ")" +
                            "   Current VLAN: " + current);

            if (_support.CurrentVlanId.HasValue)
                _vlanInput.Text = _support.CurrentVlanId.Value.ToString(CultureInfo.InvariantCulture);
            else
                _vlanInput.Text = string.Empty;

            SetStatus("VLAN support detected on " + p.RegistryKeyword + ".", Elevation.StatusText());

            UpdateActiveProfile();
            UpdateProfileButtons();
        }

/// <summary>Validates and applies the entered VLAN ID.</summary>
        private void ApplyVlan()
        {
            if (_selected == null || _support == null || !_support.Supported)
                return;

            string text = _vlanInput.Text;

            string validation = VlanInspector.ValidateId(text);
            if (validation != null)
            {
                Win95MessageBox.ShowError(this, "VLAN Configuration", validation,
                    "Valid VLAN IDs range from 1 to 4094.\n\nReserved values such as 0 (untagged) and 4095 are not accepted.");
                _vlanInput.Focus();
                _vlanInput.SelectAll();
                return;
            }

            int vlanId = int.Parse(text.Trim(), CultureInfo.InvariantCulture);

            if (!Elevation.IsElevated())
            {
                HandleNotElevated();
                return;
            }

            if (_support.CurrentVlanId.HasValue && _support.CurrentVlanId.Value == vlanId)
            {
                Win95MessageBox.ShowInfo(this, "VLAN Configuration",
                    "The adapter is already configured for VLAN " + vlanId + ".");
                return;
            }

            bool restart = Win95MessageBox.Ask(this, "VLAN Configuration",
                "Apply VLAN " + vlanId + " to " + _selected.Name + "?",
                "Some drivers only activate the new VLAN after the adapter is restarted.\n\n" +
                "Yes - apply and restart the adapter\n" +
                "No  - apply without restarting\n\n" +
                "Previous value: " +
                (_support.CurrentVlanId.HasValue
                    ? _support.CurrentVlanId.Value.ToString(CultureInfo.InvariantCulture)
                    : "none"));

            SetStatus("Applying VLAN " + vlanId + "...", Elevation.StatusText());
            Refresh();

            OperationResult result = _service.ApplyVlan(_selected, _support, vlanId, restart);

            ReportResult(result, "VLAN " + vlanId + " applied successfully.");

            // Re-read so the UI reflects the verified state.
            RefreshSelectedAdapter();
        }

        private void ResetVlan()
        {
            if (_selected == null || _support == null || !_support.Supported)
                return;

            if (!Elevation.IsElevated())
            {
                HandleNotElevated();
                return;
            }

            if (!_support.CurrentVlanId.HasValue)
            {
                Win95MessageBox.ShowInfo(this, "VLAN Configuration",
                    "No VLAN ID is currently configured on this adapter.");
                return;
            }

            if (!Win95MessageBox.Ask(this, "VLAN Configuration",
                "Remove the VLAN configuration from " + _selected.Name + "?",
                "The VLAN property will be reset to its driver default (untagged).\n\n" +
                "Current VLAN: " + _support.CurrentVlanId.Value.ToString(CultureInfo.InvariantCulture)))
                return;

            SetStatus("Resetting VLAN configuration...", Elevation.StatusText());
            Refresh();

            OperationResult result = _service.ResetVlan(_selected, _support, false);
            ReportResult(result, "VLAN configuration reset successfully.");

            RefreshSelectedAdapter();
        }

        private void ReportResult(OperationResult result, string successMessage)
        {
            if (result.Success)
            {
                SetStatus(successMessage, Elevation.StatusText());
                Win95MessageBox.ShowInfo(this, "VLAN Configuration", successMessage, result.Detail);
                return;
            }

            SetStatus("Operation failed.", "Press Details for more information.");
            Win95MessageBox.ShowError(this, "VLAN Configuration", result.Message, result.Detail);
        }

        private void HandleNotElevated()
        {
            bool elevate = Win95MessageBox.Ask(this, "Administrator Privileges",
                "Administrator privileges are required to change network adapter settings.",
                "The VLAN configuration could not be changed because this utility is not running elevated.\n\n" +
                "Yes - restart with administrator privileges");

            if (elevate)
                RequestElevation();
        }

        private void RequestElevation()
        {
            SetStatus("Requesting administrator privileges...", string.Empty);
            Refresh();

            // Mark the handover BEFORE starting the new process, so this instance
            // releases the single-instance mutex and the elevated window can open
            // instead of reporting "already running".
            Elevation.ReleaseSingleInstance();

            if (Elevation.RelaunchElevated())
            {
                Close();
                return;
            }

            // The user declined the UAC prompt: stay open and keep the mutex.
            Elevation.RelaunchPending = false;

            SetStatus("Administrator privileges required.", "Elevation was cancelled or failed.");
            Win95MessageBox.ShowWarning(this, "Administrator Privileges",
                "Administrator privileges are required to change network adapter settings.",
                "The elevation request was cancelled or could not be completed.");
        }

        private void ShowAbout()
        {
            string logLine = Log.Enabled
                ? "Logging is enabled. Log file:\n" + (Log.FilePath ?? "(not created yet)")
                : "Logging is currently disabled. Enable it under File -> Options.";

            Win95MessageBox.Show(this, "About",
                "VLAN Configuration Utility",
                "Version " + ApplicationVersion() + "\n\n" +
                "A Windows 95 style administrator utility for configuring\n" +
                "IEEE 802.1Q VLAN IDs on local network adapters.\n\n" +
                "Developed by:\n" +
                "    Khaled Nabo\n" +
                "    geissler-IT\n\n" +
                "Built with:\n" +
                "    " + FrameworkDescription() + "\n" +
                "    Windows PowerShell " + PowerShellVersion() + "\n\n" +
                "Copyright (c) " + BuildYear() + " geissler-IT\n\n" +
                "Detection uses Get-NetAdapterAdvancedProperty and matches the\n" +
                "driver RegistryKeyword (not the localised display name).\n\n" +
                logLine,
                Win95Icon.Information);
        }

        private static string ApplicationVersion()
        {
            try
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                return v != null
                    ? v.Major + "." + v.Minor + "." + v.Build
                    : "1.0";
            }
            catch
            {
                return "1.0";
            }
        }

        /// <summary>
        /// Reports the framework actually loaded at runtime rather than the one the
        /// project targets, so the About box never overstates what is running.
        /// </summary>
        private static string FrameworkDescription()
        {
            try
            {
                // ".NET Framework 4.8.9037" etc. Falls back to the CLR version below.
                string description = RuntimeInformation.FrameworkDescription;
                if (!string.IsNullOrEmpty(description)) return description;
            }
            catch
            {
                // RuntimeInformation is unavailable on very old runtimes.
            }

            Version clr = Environment.Version;
            return "Microsoft .NET Framework " + clr.Major + "." + clr.Minor +
                   " (CLR " + clr.Major + "." + clr.Minor + "." + clr.Build + ")";
        }

        /// <summary>
        /// Asks the installed PowerShell for its version through the same runner the
        /// rest of the utility uses, rather than referencing System.Management.Automation
        /// in-process - that keeps the EXE free of an extra runtime dependency.
        /// </summary>
        private static string PowerShellVersion()
        {
            try
            {
                PowerShellResult result = PowerShellRunner.Run(
                    "$PSVersionTable.PSVersion.ToString()");

                if (result != null && result.Success)
                {
                    string version = (result.Output ?? string.Empty).Trim();
                    if (version.Length > 0) return version;
                }
            }
            catch
            {
                // The About box must always open.
            }

            return "5.1 or later";
        }

        /// <summary>
        /// Uses the build timestamp of the executable so the copyright year matches
        /// the build, and never goes backwards if the file is simply copied.
        /// </summary>
        private static int BuildYear()
        {
            try
            {
                string exe = Application.ExecutablePath;
                if (!string.IsNullOrEmpty(exe) && System.IO.File.Exists(exe))
                {
                    int year = System.IO.File.GetLastWriteTime(exe).Year;
                    if (year >= 2000 && year <= 2100) return year;
                }
            }
            catch
            {
                // Fall through to the current year.
            }

            return DateTime.Now.Year;
        }

        private void ShowOptions()
        {
            using (OptionsForm form = new OptionsForm())
            {
                form.ShowDialog(this);

                // Reflect live changes in the menu check mark too.
                if (_loggingMenuItem != null) _loggingMenuItem.Checked = Log.Enabled;

                // Refresh the on-screen hint in case logging was switched on or off.
                UpdatePrivilegeDisplay();
            }
        }

        /// <summary>Flips persistent logging straight from the Defaults menu.</summary>
        private void ToggleLogging()
        {
            Log.Enabled = !Log.Enabled;
            Settings.Save();

            if (_loggingMenuItem != null) _loggingMenuItem.Checked = Log.Enabled;

            UpdatePrivilegeDisplay();
            SetStatus(Log.Enabled ? "Logging enabled." : "Logging disabled.", Elevation.StatusText());
        }

        /// <summary>
        /// Resets the persistent preferences. Saved profiles are deliberately kept -
        /// they are the user's own work, not a setting.
        /// </summary>
        private void RestoreDefaults()
        {
            if (!Win95MessageBox.Ask(this, "Defaults",
                    "Restore factory settings?",
                    "Logging will be switched off and all other options reset.\n\n" +
                    "Your saved network profiles are not affected."))
            {
                return;
            }

            Log.Enabled = false;
            Settings.Save();

            if (_loggingMenuItem != null) _loggingMenuItem.Checked = false;

            UpdatePrivilegeDisplay();
            SetStatus("Default settings restored.", Elevation.StatusText());

            Win95MessageBox.ShowInfo(this, "Defaults",
                "Default settings restored.",
                "Logging is off and all options are back to their defaults.\n\n" +
                "Your saved network profiles were kept.");
        }

        private void ShowDetails()
        {
            using (DetailsForm form = new DetailsForm(_service, _selected, _support))
                form.ShowDialog(this);
        }

        /// <summary>
        /// True when an adapter should be hidden unless "show all interfaces" is on:
        /// virtual switches, VPN/tunnel adapters, WAN miniports, IP-HTTPS and
        /// loopback interfaces. Physical Ethernet, Wi-Fi, USB Ethernet and mobile
        /// broadband are kept.
        /// </summary>
        private static bool IsNonPhysical(AdapterInfo a)
        {
            if (a == null) return true;

            if (a.IsVirtual ||
                a.Category == AdapterCategory.Vpn ||
                a.Category == AdapterCategory.HyperV ||
                a.Category == AdapterCategory.Virtual ||
                a.Category == AdapterCategory.Loopback)
            {
                return true;
            }

            // Some system-generated interfaces are not flagged as virtual, so match
            // their descriptions too.
            string description = a.Description ?? string.Empty;
            string name = a.Name ?? string.Empty;

            string[] markers =
            {
                "WAN Miniport", "IP-HTTPS", "Loopback", "Tunnel",
                "Npcap", "Bluetooth Network Connection", "TAP-Windows", "Wintun",
                "Local Area Connection*", "Mobile Broadband"
            };

            for (int i = 0; i < markers.Length; i++)
            {
                if (description.IndexOf(markers[i], StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf(markers[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Builds the secondary list row: type, status and IPv4.</summary>
        private static string DescribeAdapter(AdapterInfo a)
        {
            string status = string.IsNullOrEmpty(a.Status) ? "Unknown" : a.Status;
            string ip = string.IsNullOrEmpty(a.IPv4Addresses) ? "no IPv4" : a.IPv4Addresses;
            return a.CategoryLabel + " - " + status + " - " + ip;
        }

        /// <summary>Refreshes the elevation notice and the log path hint.</summary>
        private void UpdatePrivilegeDisplay()
        {
            bool elevated = Elevation.IsElevated();

            Control[] labels = ClientSurface.Controls.Find("ElevationLabel", true);
            if (labels.Length > 0)
                ((Label)labels[0]).Text = elevated
                    ? "Running with administrator privileges."
                    : "Not running as administrator.";

            Control[] buttons = ClientSurface.Controls.Find("ElevateButton", true);
            if (buttons.Length > 0)
                buttons[0].Enabled = !elevated;

            string logPath = Log.FilePath;
            Control[] logLabels = ClientSurface.Controls.Find("LogLabel", true);
            if (logLabels.Length > 0)
            {
                if (!Log.Enabled)
                {
                    ((Label)logLabels[0]).Text =
                        "Logging is disabled.\n\nEnable it under File -> Options.";
                }
                else if (!string.IsNullOrEmpty(logPath))
                {
                    ((Label)logLabels[0]).Text = "A log of each operation is written to:\n\n" + logPath;
                }
                else
                {
                    ((Label)logLabels[0]).Text = "Logging is enabled.";
                }
            }
        }

        /// <summary>
        /// Compact profile strip under the VLAN group: pick a profile and apply it
        /// without opening the full editor.
        /// </summary>
        private void BuildProfilePanel(Panel root)
        {
            // Sits below the VLAN group (which ends at y=462) so the two never overlap,
            // and spans the full content width. Anchored to the bottom so the panel
            // stays put when the window is resized or maximised.
            ClassicGroupBox group = new ClassicGroupBox();
            group.Text = "Network Profile";
            group.SetBounds(12, 470, 596, 78);
            group.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            root.Controls.Add(group);

            _profileCombo = new ComboBox();
            _profileCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _profileCombo.Font = Win95Style.CreateFontSafe();
            _profileCombo.SetBounds(14, 22, 250, 20);
            _profileCombo.SelectedIndexChanged += delegate { OnProfileComboChanged(); };
            group.Controls.Add(_profileCombo);

            _applyProfileButton = new ClassicButton();
            _applyProfileButton.Text = "&Apply Profile";
            _applyProfileButton.SetBounds(272, 20, 92, 23);
            _applyProfileButton.Enabled = false;
            _applyProfileButton.Click += delegate { ApplySelectedProfile(); };
            group.Controls.Add(_applyProfileButton);

            _restoreButton = new ClassicButton();
            _restoreButton.Text = "&Restore Previous";
            _restoreButton.SetBounds(370, 20, 116, 23);
            _restoreButton.Enabled = false;
            _restoreButton.Click += delegate { RestorePreviousConfiguration(); };
            group.Controls.Add(_restoreButton);

            _saveProfileButton = new ClassicButton();
            _saveProfileButton.Text = "&Manage Profiles...";
            _saveProfileButton.SetBounds(492, 20, 100, 23);
            _saveProfileButton.Click += delegate { ShowProfiles(); };
            group.Controls.Add(_saveProfileButton);

            _activeProfileLabel = new Label();
            _activeProfileLabel.Name = "ActiveProfileLabel";
            _activeProfileLabel.Font = Win95Style.CreateFontSafe();
            _activeProfileLabel.BackColor = Win95Style.Face;
            _activeProfileLabel.ForeColor = Win95Style.WindowText;
            _activeProfileLabel.AutoSize = false;
            _activeProfileLabel.TextAlign = ContentAlignment.MiddleLeft;

            // Full width on its own line, so a long profile name is never clipped.
            _activeProfileLabel.SetBounds(14, 48, 568, 18);
            _activeProfileLabel.Text = "Active: -";
            group.Controls.Add(_activeProfileLabel);
        }

        /// <summary>Loads profiles from disk once, then fills the combo box.</summary>
        private void EnsureProfilesLoaded()
        {
            if (_profilesLoaded) return;
            _profilesLoaded = true;

            string error;
            ProfileStore.Load(out error);

            if (error != null)
            {
                SetStatus("Profiles could not be loaded.", "See File -> Profiles for details.");
            }

            _applier = new ProfileApplier(_service);
            RefreshProfileCombo();
        }

        private void RefreshProfileCombo()
        {
            if (_profileCombo == null) return;

            _suppressProfileEvents = true;
            _profileCombo.Items.Clear();

            foreach (NetworkProfile p in ProfileStore.Profiles)
                _profileCombo.Items.Add(p.Summary);

            _profileCombo.SelectedIndex = ProfileStore.Profiles.Count > 0 ? 0 : -1;
            _suppressProfileEvents = false;

            UpdateProfileButtons();
        }

        private NetworkProfile SelectedProfile()
        {
            int index = _profileCombo != null ? _profileCombo.SelectedIndex : -1;
            if (index < 0 || index >= ProfileStore.Profiles.Count) return null;
            return ProfileStore.Profiles[index];
        }

        private void OnProfileComboChanged()
        {
            if (_suppressProfileEvents) return;
            UpdateProfileButtons();
        }

        internal void UpdateProfileButtons()
        {
            bool haveProfile = SelectedProfile() != null;
            bool haveAdapter = _selected != null;

            if (_applyProfileButton != null)
                _applyProfileButton.Enabled = haveProfile && haveAdapter && !ProfileRunner.IsBusy;

            if (_saveProfileButton != null)
                _saveProfileButton.Enabled = !ProfileRunner.IsBusy;

            if (_restoreButton != null)
                _restoreButton.Enabled = _applier != null && haveAdapter &&
                    _applier.CanRestore(_selected) && !ProfileRunner.IsBusy;
        }


        private void ApplySelectedProfile()
        {
            EnsureProfilesLoaded();

            if (_selected == null)
            {
                Win95MessageBox.ShowWarning(this, "Network Profiles",
                    "Select a network adapter first.", "Choose an adapter in the list on the left.");
                return;
            }

            NetworkProfile profile = SelectedProfile();
            if (profile == null) return;

            ProfileRunner.Run(this, _service, _selected, _support, profile, true);
        }

        private void RestorePreviousConfiguration()
        {
            if (_applier == null || _selected == null) return;

            ThreadPool.QueueUserWorkItem(delegate
            {
                ApplyReport report = null;
                try
                {
                    report = _applier.RestorePrevious(_selected, _support, delegate(string m)
                    {
                        return true;
                    });
                }
                catch (Exception ex)
                {
                    report = new ApplyReport();
                    report.Success = false;
                    report.Message = "The previous configuration could not be restored: " + ex.Message;
                }

                ApplyReport final = report;
                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        SetStatus(final.Message, Elevation.StatusText());
                        Win95MessageBox.ShowInfo(this, "Network Profiles", final.Message, final.Detail);
                        UpdateProfileButtons();
                        RefreshSelectedAdapter();
                        UpdateActiveProfile();
                    }));
                }
                catch
                {
                    // Window may have closed while the worker ran.
                }
            });
        }

        private void ShowProfiles()
        {
            EnsureProfilesLoaded();

            using (ProfileForm form = new ProfileForm())
            {
                form.ProfilesChanged += delegate { RefreshProfileCombo(); };
                form.ShowDialog(this);
            }

            RefreshProfileCombo();
        }

        /// <summary>Reads the real configuration and reports which profile matches.</summary>
        private void UpdateActiveProfile()
        {
            if (_activeProfileLabel == null) return;

            if (_applier == null || _selected == null)
            {
                _activeProfileLabel.Text = "Active: -";
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                string readError;
                AdapterSnapshot snap = _applier.ReadState(_selected, out readError);
                if (snap == null) return;

                string match = _applier.FindMatchingProfile(snap, _support);
                string text = match != null ? match : "Custom / Unknown";

                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        if (_activeProfileLabel != null && !_activeProfileLabel.IsDisposed)
                            _activeProfileLabel.Text = "Active: " + text;
                    }));
                }
                catch
                {
                    // Window closed while reading.
                }
            });
        }
    }
}
