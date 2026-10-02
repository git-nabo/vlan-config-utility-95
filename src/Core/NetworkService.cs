using System;
using System.Collections.Generic;
using System.Globalization;

namespace VlanConfig95.Core
{
    /// <summary>Outcome of a VLAN operation, with a user-presentable message.</summary>
    public sealed class OperationResult
    {
        public bool Success;
        public string Message;
        public string Detail;

        public OperationResult(bool success, string message, string detail)
        {
            Success = success;
            Message = message;
            Detail = detail ?? string.Empty;
        }
    }

    /// <summary>
    /// Application-facing service: enumerate adapters, detect VLAN capability,
    /// apply and reset VLAN IDs, with verification and rollback.
    /// </summary>
    public sealed class NetworkService
    {
        private readonly List<string> _lastCommand = new List<string>();
        private readonly System.Text.StringBuilder _details = new System.Text.StringBuilder();

        /// <summary>Raw diagnostics for the Details window.</summary>
        public string LastRawDetails
        {
            get { return _details.ToString(); }
        }

        public IList<string> LastCommands
        {
            get { return _lastCommand; }
        }

        private void BeginCommand()
        {
            _lastCommand.Clear();
            _details.Length = 0;
        }

        private void Record(PowerShellResult ps, string humanCommand, string script)
        {
            _lastCommand.Add(humanCommand);
            _lastCommand.Add(script);

            _details.AppendLine("---- " + humanCommand + " ----");
            _details.AppendLine(script);
            _details.AppendLine("[exit code] " + ps.ExitCode);
            _details.AppendLine("[stdout]");
            _details.AppendLine(ps.Output);
            _details.AppendLine("[stderr]");
            _details.AppendLine(ps.Error);
            _details.AppendLine();
        }
/// <summary>
        /// Enumerates adapters. Returns an empty list and sets error when the
        /// enumeration failed (for example, when PowerShell is unavailable).
        /// </summary>
        public List<AdapterInfo> EnumerateAdapters(out string error)
        {
            error = null;
            BeginCommand();

            string psVersion;
            if (!PowerShellRunner.IsAvailable(out psVersion))
            {
                error = "Windows PowerShell could not be found. " +
                        "The NetAdapter cmdlets require Windows PowerShell 5.1 or later.";
                return new List<AdapterInfo>();
            }

            string script = PsScripts.EnumerateAdapters();
            PowerShellResult ps = PowerShellRunner.Run(script);
            Record(ps, "Get-NetAdapter -IncludeHidden", script);

            if (!ps.Success)
            {
                error = DescribeFailure(ps, "Could not enumerate network adapters.");
                return new List<AdapterInfo>();
            }

            RecordSet set = RecordSetParser.Parse(PowerShellRunner.ExtractPayload(ps));
            if (set.Error != null)
            {
                error = set.Error;
                return new List<AdapterInfo>();
            }

            List<AdapterInfo> adapters = new List<AdapterInfo>();
            foreach (Record r in set.OfKind("A"))
            {
                AdapterInfo a = new AdapterInfo();
                a.InterfaceIndex = ParseInt(r.Get(0));
                a.Name = r.Get(1);
                a.Description = r.Get(2);
                a.Status = r.Get(3);
                a.MacAddress = r.Get(4);
                a.LinkSpeed = r.Get(5);
                a.MediaType = r.Get(6);
                a.PhysicalMediaType = r.Get(7);
                a.DriverProvider = r.Get(8);
                a.DriverVersion = r.Get(9);
                a.DriverFileName = r.Get(10);
                a.DriverDate = r.Get(11);
                a.ComponentId = r.Get(12);
                a.PnpDeviceId = r.Get(13);
                a.InterfaceGuid = r.Get(14);
                a.IPv4Addresses = r.Get(15);
                a.IPv6Addresses = r.Get(16);
                a.IsVirtual = string.Equals(r.Get(17), "True", StringComparison.OrdinalIgnoreCase);

                Classify(a);
                adapters.Add(a);
            }

            adapters.Sort(delegate(AdapterInfo x, AdapterInfo y)
            {
                if (x.IsConnected != y.IsConnected)
                    return x.IsConnected ? -1 : 1;

                return x.InterfaceIndex.CompareTo(y.InterfaceIndex);
            });

            Log.Write("ENUMERATE", "all adapters", "-", "-", "found " + adapters.Count + " adapters");
            return adapters;
        }

        private static int ParseInt(string text)
        {
            int value;
            if (VlanInspector.TryInt(text, out value))
                return value;
            return -1;
        }
/// <summary>
        /// Classifies an adapter so the UI can distinguish physical NICs from
        /// Wi-Fi, virtual, VPN/tap and Hyper-V interfaces.
        /// </summary>
        private static void Classify(AdapterInfo a)
        {
            string haystack = ((a.Description ?? string.Empty) + " " +
                               (a.Name ?? string.Empty) + " " +
                               (a.MediaType ?? string.Empty) + " " +
                               (a.PhysicalMediaType ?? string.Empty)).ToLowerInvariant();

            string driver = (a.DriverFileName ?? string.Empty).ToLowerInvariant();
            string compId = (a.ComponentId ?? string.Empty).ToLowerInvariant();

            if (haystack.Contains("hyper-v") || driver.Contains("vmsproxy") ||
                compId.Contains("vms_mp") ||
                (a.Name ?? string.Empty).StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase))
            {
                a.Category = AdapterCategory.HyperV;
                a.CategoryLabel = "Hyper-V";
            }
            else if (haystack.Contains("tap-windows") || haystack.Contains("tunnel") ||
                     haystack.Contains("wintun") || haystack.Contains("wireguard") ||
                     haystack.Contains("vpn") || driver.Contains("tap") || driver.Contains("wintun"))
            {
                a.Category = AdapterCategory.Vpn;
                a.CategoryLabel = "VPN / Tunnel";
            }
            else if (haystack.Contains("wi-fi") || haystack.Contains("wireless") ||
                     haystack.Contains("802.11") || a.MediaType == "Native 802.11" ||
                     driver.Contains("wlt"))
            {
                a.Category = AdapterCategory.Wireless;
                a.CategoryLabel = "Wireless";
            }
            else if (haystack.Contains("bluetooth"))
            {
                a.Category = AdapterCategory.Bluetooth;
                a.CategoryLabel = "Bluetooth";
            }
            else if (haystack.Contains("wan miniport") || a.MediaType == "Wireless WAN")
            {
                a.Category = AdapterCategory.Wwan;
                a.CategoryLabel = "WAN Miniport";
            }
            else if (a.IsVirtual || haystack.Contains("virtual") || driver.Contains("vmxnet") ||
                     driver.Contains("vbox") || driver.Contains("vmnet"))
            {
                a.Category = AdapterCategory.Virtual;
                a.CategoryLabel = "Virtual";
            }
            else if (haystack.Contains("loopback") || driver.Contains("loopback") ||
                     haystack.Contains("pseudo"))
            {
                a.Category = AdapterCategory.Loopback;
                a.CategoryLabel = "Loopback";
            }
            else
            {
                a.Category = AdapterCategory.Ethernet;
                a.CategoryLabel = a.IsVirtual ? "Ethernet (virtual)" : "Ethernet";
            }
        }

        /// <summary>
        /// Inspects an adapter and returns genuine VLAN capability. Failures are
        /// reported through the error parameter rather than thrown.
        /// </summary>
        public VlanSupport InspectVlan(AdapterInfo adapter, out string error)
        {
            error = null;
            BeginCommand();

            if (adapter == null)
            {
                error = "No adapter was selected.";
                return new VlanSupport();
            }

            string script = PsScripts.ReadVlanInfo(adapter.InterfaceIndex);
            PowerShellResult ps = PowerShellRunner.Run(script);
            Record(ps, "Get-NetAdapterAdvancedProperty -AllProperties / MSFT_NetAdapter", script);

            if (!ps.Success)
            {
                error = DescribeFailure(ps, "Could not read adapter properties.");
                return new VlanSupport();
            }

            RecordSet set = RecordSetParser.Parse(PowerShellRunner.ExtractPayload(ps));
            if (set.Error != null)
            {
                error = set.Error;
                return new VlanSupport();
            }

            VlanSupport support = VlanInspector.Inspect(set);

            Log.Write("INSPECT", adapter.LogKey, support.CurrentValueText, "-",
                      support.Supported
                          ? "supported via " + support.Property.RegistryKeyword
                          : "not supported (" + support.Rejected.Count + " candidate(s) rejected)");

            return support;
        }

        /// <summary>
        /// Applies a VLAN ID, then verifies Windows accepted the change. On
        /// verification failure the previous value is restored (best effort).
        /// </summary>
        public OperationResult ApplyVlan(AdapterInfo adapter, VlanSupport support, int vlanId, bool restartAdapter)
        {
            if (adapter == null || support == null || !support.Supported)
                return new OperationResult(false, "This adapter does not support VLAN configuration.", string.Empty);

            string validation = VlanInspector.ValidateId(vlanId.ToString(CultureInfo.InvariantCulture));
            if (validation != null)
                return new OperationResult(false, validation, string.Empty);

            if (!Elevation.IsElevated())
            {
                Log.Write("APPLY", adapter.LogKey, support.CurrentValueText, vlanId.ToString(), "denied: not elevated");
                return new OperationResult(false,
                    "Administrator privileges are required to change adapter settings.", string.Empty);
            }

            string previousText = string.IsNullOrEmpty(support.CurrentValueText)
                ? "(none)"
                : support.CurrentValueText;

            string script = PsScripts.SetVlanProperty(adapter.InterfaceIndex, support.Property.RegistryKeyword, vlanId, !restartAdapter);
            PowerShellResult ps = PowerShellRunner.Run(script);
            Record(ps, "Set-NetAdapterAdvancedProperty -RegistryValue " + vlanId, script);

            if (!ps.Success)
            {
                string message = DescribeFailure(ps, "The driver rejected VLAN " + vlanId + ".");
                Log.Write("APPLY", adapter.LogKey, previousText, vlanId.ToString(), "failed: " + message);
                return new OperationResult(false, message, ps.Error);
            }

            System.Threading.Thread.Sleep(restartAdapter ? 1500 : 250);

            string verifyError;
            VlanSupport verify = InspectVlan(adapter, out verifyError);
            int? actual = verify.CurrentVlanId;

            if (actual.HasValue && actual.Value == vlanId)
            {
                Log.Write("APPLY", adapter.LogKey, previousText, vlanId.ToString(), "verified OK");
                return new OperationResult(true,
                    "VLAN configuration updated successfully.",
                    "Verified VLAN " + vlanId + " on " + adapter.Name + ".");
            }

            Log.Write("APPLY", adapter.LogKey, previousText, vlanId.ToString(),
                      "verification failed (actual=" +
                      (actual.HasValue ? actual.Value.ToString() : "?") + "); rolling back");

            string detail = verifyError != null
                ? verifyError
                : "Windows reported success but the value did not change. The driver may require an adapter restart.";

            Rollback(adapter, support);

            return new OperationResult(false,
                "The driver accepted the request but the VLAN ID was not applied. The previous value was restored.",
                detail);
        }

        /// <summary>Restores the previous value after a failed apply.</summary>
        private void Rollback(AdapterInfo adapter, VlanSupport support)
        {
            int previous;
            if (!VlanInspector.TryInt(support.CurrentValueText, out previous))
                previous = 0;

            string script = PsScripts.SetVlanProperty(adapter.InterfaceIndex, support.Property.RegistryKeyword, previous, true);
            PowerShellResult ps = PowerShellRunner.Run(script);
            Record(ps, "ROLLBACK Set-NetAdapterAdvancedProperty -RegistryValue " + previous, script);

            Log.Write("ROLLBACK", adapter.LogKey, support.CurrentValueText, previous.ToString(),
                      ps.Success ? "restored" : "rollback failed");
        }
/// <summary>Resets the VLAN property to the driver default (normally 0 / untagged).</summary>
        public OperationResult ResetVlan(AdapterInfo adapter, VlanSupport support, bool restartAdapter)
        {
            if (adapter == null || support == null || !support.Supported)
                return new OperationResult(false, "This adapter does not support VLAN configuration.", string.Empty);

            if (!Elevation.IsElevated())
            {
                Log.Write("RESET", adapter.LogKey, support.CurrentValueText, "0", "denied: not elevated");
                return new OperationResult(false,
                    "Administrator privileges are required to change adapter settings.", string.Empty);
            }

            string previousText = string.IsNullOrEmpty(support.CurrentValueText)
                ? "(none)"
                : support.CurrentValueText;

            string script = PsScripts.ResetVlanProperty(adapter.InterfaceIndex, support.Property.RegistryKeyword, !restartAdapter);
            PowerShellResult ps = PowerShellRunner.Run(script);
            Record(ps, "Set-NetAdapterAdvancedProperty -RegistryValue 0 (reset)", script);

            if (!ps.Success)
            {
                string message = DescribeFailure(ps, "The driver rejected the VLAN reset.");
                Log.Write("RESET", adapter.LogKey, previousText, "0", "failed: " + message);
                return new OperationResult(false, message, ps.Error);
            }

            System.Threading.Thread.Sleep(restartAdapter ? 1500 : 250);

            string verifyError;
            VlanSupport verify = InspectVlan(adapter, out verifyError);

            if (!verify.CurrentVlanId.HasValue)
            {
                Log.Write("RESET", adapter.LogKey, previousText, "0", "verified: no VLAN");
                return new OperationResult(true,
                    "VLAN configuration reset successfully.",
                    "The adapter is no longer tagged with a VLAN ID.");
            }

            Log.Write("RESET", adapter.LogKey, previousText, "0", "verification failed");
            Rollback(adapter, support);

            return new OperationResult(false,
                "The reset was not applied by the driver. The previous value was restored.",
                verifyError ?? "The adapter still reports VLAN " + verify.CurrentVlanId.Value + ".");
        }

        private static string DescribeFailure(PowerShellResult ps, string headline)
        {
            string raw = !string.IsNullOrEmpty(ps.Error) ? ps.Error : ps.Output;
            return headline + " " + CleanError(raw);
        }

        /// <summary>
        /// Reduces a PowerShell/CIM error to one readable sentence, preserving the
        /// original text when it carries extra detail.
        /// </summary>
        public static string CleanError(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return "(no further detail was reported)";

            string text = raw.Trim();
            string[] lines = text.Replace("\r\n", "\n").Split(new[] { '\n' });
            System.Text.StringBuilder sb = new System.Text.StringBuilder();

            foreach (string line in lines)
            {
                string t = line.Trim();
                if (t.Length == 0)
                    continue;

                // Drop PowerShell's line/category scaffolding, keep the message.
                if (t.StartsWith("In Zeile:", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("At line:", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("CategoryInfo", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("FullyQualifiedErrorId", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(t);
            }

            string result = sb.ToString().Trim();
            return result.Length == 0 ? text : result;
        }
    }
}