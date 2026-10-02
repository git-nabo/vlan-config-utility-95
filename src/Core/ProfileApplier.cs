using System;
using System.Collections.Generic;
using System.Globalization;

namespace VlanConfig95.Core
{
    /// <summary>Snapshot of an adapter's IP-level configuration.</summary>
    public sealed class AdapterSnapshot
    {
        public int InterfaceIndex;
        public string AdapterName;
        public bool IsDhcp;
        public string IpAddress;
        public int PrefixLength;
        public string Gateway;
        public string PrimaryDns;
        public string SecondaryDns;
        public int VlanId;
        public bool HasVlan;

        public AdapterSnapshot Clone()
        {
            AdapterSnapshot s = new AdapterSnapshot();
            s.InterfaceIndex = InterfaceIndex; s.AdapterName = AdapterName; s.IsDhcp = IsDhcp;
            s.IpAddress = IpAddress; s.PrefixLength = PrefixLength; s.Gateway = Gateway;
            s.PrimaryDns = PrimaryDns; s.SecondaryDns = SecondaryDns;
            s.VlanId = VlanId; s.HasVlan = HasVlan;
            return s;
        }
    }

    /// <summary>Outcome of applying (or restoring) a profile.</summary>
    public sealed class ApplyReport
    {
        public bool Success;
        public bool Partial;
        public string Message;
        public string Detail;
        public List<string> Steps = new List<string>();
        public List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// Applies a profile to an adapter, reusing the existing VLAN implementation.
    ///
    /// Order of operations (the adapter can briefly disappear after a VLAN change):
    ///   1. validate the profile
    ///   2. read the current state (used for the backup)
    ///   3. apply the VLAN (if the profile has one and the driver supports it)
    ///   4. wait for the adapter to return
    ///   5. apply DHCP or static IPv4 + gateway
    ///   6. apply DNS
    ///   7. re-read and verify
    /// </summary>
    public sealed class ProfileApplier
    {
        private readonly NetworkService _network;

        public ProfileApplier(NetworkService network)
        {
            _network = network;
        }

        /// <summary>Reports progress; return false to request cancellation.</summary>
        public delegate bool ProgressHandler(string message);

        /// <summary>Keeps the pre-apply snapshot, for "restore previous".</summary>
        public AdapterSnapshot LastBackup { get; private set; }

        public int LastBackupAdapterIndex { get; private set; }

        /// <summary>True when a restore is possible for the given adapter.</summary>
        public bool CanRestore(AdapterInfo adapter)
        {
            return LastBackup != null && adapter != null &&
                   adapter.InterfaceIndex == LastBackupAdapterIndex;
        }

        /// <summary>Reads the current IP/DNS/gateway configuration of an adapter.</summary>
        public AdapterSnapshot ReadState(AdapterInfo adapter, out string error)
        {
            error = null;
            if (adapter == null) { error = "No adapter was selected."; return null; }

            PowerShellResult ps = PowerShellRunner.Run(ProfileScripts.ReadState(adapter.InterfaceIndex));

            if (!ps.Success)
            {
                error = DescribeFailure(ps);
                return null;
            }

            RecordSet set = RecordSetParser.Parse(PowerShellRunner.ExtractPayload(ps));
            if (set.Error != null) { error = set.Error; return null; }

            foreach (Record r in set.OfKind("S"))
            {
                AdapterSnapshot snap = new AdapterSnapshot();
                int idx;
                snap.InterfaceIndex = VlanInspector.TryInt(r.Get(0), out idx) ? idx : -1;
                snap.AdapterName = r.Get(1);
                snap.IsDhcp = string.Equals(r.Get(2), "Enabled", StringComparison.OrdinalIgnoreCase);
                snap.IpAddress = r.Get(3);
                int prefix;
                snap.PrefixLength = VlanInspector.TryInt(r.Get(4), out prefix) ? prefix : 0;
                snap.Gateway = r.Get(5);
                snap.PrimaryDns = r.Get(6);
                snap.SecondaryDns = r.Get(7);
                return snap;
            }

            error = "The adapter did not report a configuration.";
            return null;
        }

        /// <summary>
        /// Turns a failed script run into a message fit for a dialog.
        ///
        /// The scripts report failures through the ###VLANCFG payload as
        /// "!ERR|message". Showing raw stdout/stderr instead leaked those internal
        /// protocol markers to the user, so the payload is parsed first and the raw
        /// text is only used as a fallback.
        /// </summary>
        private static string DescribeFailure(PowerShellResult result)
        {
            if (result == null)
                return "(no information was returned)";

            RecordSet set = RecordSetParser.Parse(PowerShellRunner.ExtractPayload(result));
            if (set != null && !string.IsNullOrEmpty(set.Error))
                return set.Error.Trim();

            string raw = (result.Error != null && result.Error.Length > 0)
                ? result.Error
                : result.Output;

            return NetworkService.CleanError(raw);
        }

        private static bool SameAddress(string a, string b)
        {
            string x = (a ?? string.Empty).Trim();
            string y = (b ?? string.Empty).Trim();
            if (x.Length == 0 && y.Length == 0) return true;
            if (x.Length == 0 || y.Length == 0) return false;
            return string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Finds the saved profile matching the adapter's real current configuration.
        /// Never assumes a profile is active merely because it was applied last.
        /// </summary>
        public string FindMatchingProfile(AdapterSnapshot snapshot, VlanSupport support)
        {
            if (snapshot == null) return null;

            foreach (NetworkProfile p in ProfileStore.Profiles)
            {
                if (p.IpConfig != (snapshot.IsDhcp ? IpMode.Dhcp : IpMode.Static)) continue;
                if (p.IpConfig == IpMode.Static && !SameAddress(p.IpAddress, snapshot.IpAddress)) continue;

                // A DHCP profile takes its gateway and DNS from the DHCP server, so
                // the values it stores are empty by design. Comparing those against
                // whatever the server handed out could never match, so a DHCP profile
                // is identified by its DHCP state alone.
                if (p.IpConfig != IpMode.Dhcp && !SameAddress(p.Gateway, snapshot.Gateway)) continue;

                bool wantsAutomatic = (p.DnsConfig == DnsMode.Automatic);

                // DNS servers only count as "static" when the interface is not using
                // DHCP. A DHCP adapter always reports the servers it was handed, so
                // treating those as static would make "Automatic DNS" never match.
                bool hasStaticDns = !snapshot.IsDhcp && !string.IsNullOrEmpty(snapshot.PrimaryDns);

                if (wantsAutomatic && hasStaticDns) continue;

                if (!wantsAutomatic && !snapshot.IsDhcp && !SameAddress(p.PrimaryDns, snapshot.PrimaryDns))
                    continue;

                if (support != null && support.Supported && support.CurrentVlanId.HasValue)
                {
                    if (p.HasVlan && support.CurrentVlanId.Value != p.VlanValue) continue;
                }

                return p.Name;
            }

            return null;
        }

        private static int ResolvePrefix(NetworkProfile profile)
        {
            int prefix;
            if (!string.IsNullOrEmpty(profile.PrefixLength) &&
                VlanInspector.TryInt(profile.PrefixLength, out prefix) && prefix >= 0 && prefix <= 32)
                return prefix;

            if (!string.IsNullOrEmpty(profile.SubnetMask) && NetworkProfile.IsValidSubnetMask(profile.SubnetMask))
                return NetworkProfile.MaskToPrefix(profile.SubnetMask);

            return 24;
        }

        private static bool Matches(AdapterSnapshot actual, NetworkProfile profile)
        {
            bool expectDhcp = (profile.IpConfig == IpMode.Dhcp);
            if (expectDhcp != actual.IsDhcp) return false;

            if (!expectDhcp &&
                !string.Equals(actual.IpAddress, profile.IpAddress, StringComparison.OrdinalIgnoreCase))
                return false;

            if (profile.DnsConfig == DnsMode.Automatic)
            {
                // A DHCP interface always lists servers; only a static interface
                // should be free of them.
                if (!actual.IsDhcp && !string.IsNullOrEmpty(actual.PrimaryDns)) return false;
            }
            else if (!string.Equals(actual.PrimaryDns, profile.PrimaryDns, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Applies a profile. Returns a report describing exactly what happened.
        /// Nothing is changed when validation fails.
        /// </summary>
        public ApplyReport Apply(AdapterInfo adapter, NetworkProfile profile, VlanSupport support,
                                 ProgressHandler progress)
        {
            ApplyReport report = new ApplyReport();

            if (adapter == null) { report.Message = "No adapter was selected."; return report; }
            if (profile == null) { report.Message = "No profile was selected."; return report; }

            // 1. Validate first: never partially apply an invalid profile.
            string problem = profile.Validate();
            if (problem != null)
            {
                report.Message = "The profile is not valid: " + problem;
                return report;
            }

            if (!Elevation.IsElevated())
            {
                report.Message = "Administrator privileges are required to change adapter configuration.";
                return report;
            }

            if (progress != null && !progress("Applying profile '" + profile.Name + "'..."))
            {
                report.Message = "The operation was cancelled.";
                return report;
            }

            // 2. Read the current state so it can be restored later.
            string readError;
            AdapterSnapshot before = ReadState(adapter, out readError);
            if (before != null)
            {
                LastBackup = before.Clone();
                LastBackupAdapterIndex = adapter.InterfaceIndex;
            }

            // 3. VLAN - reuse the existing implementation.
            if (profile.HasVlan)
            {
                if (progress != null && !progress("Changing VLAN to " + profile.VlanValue + "..."))
                {
                    report.Message = "The operation was cancelled.";
                    return report;
                }

                if (support == null || !support.Supported)
                {
                    report.Partial = true;
                    report.Warnings.Add("Network settings were applied, but VLAN configuration is not supported by this adapter driver.");
                    report.Steps.Add("VLAN " + profile.VlanValue + " skipped: not supported by this driver.");
                }
                else
                {
                    OperationResult vlanResult = _network.ApplyVlan(adapter, support, profile.VlanValue, true);
                    if (vlanResult.Success)
                        report.Steps.Add("VLAN set to " + profile.VlanValue + ".");
                    else
                    {
                        report.Partial = true;
                        report.Warnings.Add("The VLAN could not be changed: " + vlanResult.Message);
                        report.Steps.Add("VLAN change failed.");
                    }
                }

                // 4. The driver may have reset the adapter; wait for it to return.
                if (progress != null && !progress("Waiting for network adapter..."))
                {
                    report.Message = "The operation was cancelled.";
                    return report;
                }

                PowerShellRunner.Run(ProfileScripts.WaitForAdapter(adapter.InterfaceIndex, 10));
                report.Steps.Add("Adapter settled after the VLAN change.");
            }
            else if (support != null && support.Supported && support.CurrentVlanId.HasValue)
            {
                if (progress != null && !progress("Removing VLAN configuration..."))
                {
                    report.Message = "The operation was cancelled.";
                    return report;
                }

                OperationResult vlanResult = _network.ResetVlan(adapter, support, true);
                if (vlanResult.Success)
                    report.Steps.Add("VLAN removed (untagged).");
                else
                    report.Warnings.Add("The VLAN could not be removed: " + vlanResult.Message);
            }

            // 5. DHCP or static IPv4 + gateway.
            if (progress != null && !progress("Configuring IPv4..."))
            {
                report.Message = "The operation was cancelled.";
                return report;
            }

            string ipScript = profile.IpConfig == IpMode.Dhcp
                ? ProfileScripts.SetDhcp(adapter.InterfaceIndex)
                : ProfileScripts.SetStaticIp(adapter.InterfaceIndex, profile.IpAddress, ResolvePrefix(profile), profile.Gateway);

            PowerShellResult ipResult = PowerShellRunner.Run(ipScript);
            if (!ipResult.Success)
            {
                report.Success = false;
                report.Message = "The IPv4 configuration could not be applied. " +
                    DescribeFailure(ipResult);

                // Log the failure too: without this an apply that died here left no
                // trace at all in the log file, which made it impossible to diagnose.
                Log.Write("PROFILE-APPLY", adapter.LogKey,
                    before != null ? before.IpAddress : "-",
                    profile.IpAddress + " / VLAN " + (profile.HasVlan ? profile.VlanValue.ToString(CultureInfo.InvariantCulture) : "none"),
                    "FAILED at IPv4: " + NetworkService.CleanError(report.Message));

                return report;
            }

            report.Steps.Add(profile.IpConfig == IpMode.Dhcp
                ? "IPv4 set to DHCP."
                : "Static IPv4 " + profile.IpAddress + " applied.");

            // 6. DNS.
            if (progress != null && !progress("Configuring DNS..."))
            {
                report.Message = "The operation was cancelled.";
                return report;
            }

            bool automaticDns = (profile.DnsConfig == DnsMode.Automatic);
            PowerShellResult dnsResult = PowerShellRunner.Run(
                ProfileScripts.SetDns(adapter.InterfaceIndex, profile.PrimaryDns, profile.SecondaryDns, automaticDns));

            if (!dnsResult.Success)
            {
                report.Partial = true;
                report.Warnings.Add("The DNS servers could not be changed. " +
                    DescribeFailure(dnsResult));
                report.Steps.Add("DNS change failed.");
            }
            else
            {
                report.Steps.Add(automaticDns ? "DNS set to automatic." : "DNS set to " + profile.PrimaryDns + ".");
            }

            // 7. Re-read and verify what actually stuck.
            if (progress != null && !progress("Verifying configuration..."))
            {
                report.Message = "The operation was cancelled.";
                return report;
            }

            string verifyError;
            AdapterSnapshot after = ReadState(adapter, out verifyError);

            if (after == null)
            {
                report.Partial = true;
                report.Warnings.Add("The configuration could not be read back for verification.");
            }
            else if (!Matches(after, profile))
            {
                report.Partial = true;
                report.Warnings.Add("The adapter did not report every requested value. It may take a moment to settle, or the driver may have overridden some settings.");
            }

            Log.Write("PROFILE-APPLY", adapter.LogKey,
                before != null ? before.IpAddress : "-",
                profile.IpAddress + " / VLAN " + (profile.HasVlan ? profile.VlanValue.ToString(CultureInfo.InvariantCulture) : "none"),
                report.Partial ? "partially applied" : "verified OK");

            report.Success = true;
            report.Message = report.Partial ? "Profile partially applied - see warnings." : "Profile applied successfully.";
            report.Detail = string.Join(Environment.NewLine, report.Steps.ToArray());
            return report;
        }

        /// <summary>Re-applies the configuration captured before the last apply.</summary>
        public ApplyReport RestorePrevious(AdapterInfo adapter, VlanSupport support, ProgressHandler progress)
        {
            ApplyReport report = new ApplyReport();

            if (!CanRestore(adapter))
            {
                report.Message = "There is no previous configuration to restore for this adapter.";
                return report;
            }

            if (!Elevation.IsElevated())
            {
                report.Message = "Administrator privileges are required to change adapter configuration.";
                return report;
            }

            if (progress != null && !progress("Restoring previous configuration..."))
            {
                report.Message = "The operation was cancelled.";
                return report;
            }

            if (support != null && support.Supported && support.CurrentVlanId.HasValue)
            {
                if (LastBackup.HasVlan && support.CurrentVlanId.Value != LastBackup.VlanId)
                {
                    if (progress != null && !progress("Restoring VLAN to " + LastBackup.VlanId + "..."))
                    {
                        report.Message = "The operation was cancelled.";
                        return report;
                    }

                    _network.ApplyVlan(adapter, support, LastBackup.VlanId, true);
                    PowerShellRunner.Run(ProfileScripts.WaitForAdapter(adapter.InterfaceIndex, 10));
                }
                else if (!LastBackup.HasVlan)
                {
                    _network.ResetVlan(adapter, support, true);
                    PowerShellRunner.Run(ProfileScripts.WaitForAdapter(adapter.InterfaceIndex, 10));
                }
            }

            string ipScript = LastBackup.IsDhcp
                ? ProfileScripts.SetDhcp(adapter.InterfaceIndex)
                : ProfileScripts.SetStaticIp(adapter.InterfaceIndex, LastBackup.IpAddress, LastBackup.PrefixLength, LastBackup.Gateway);

            PowerShellResult ipResult = PowerShellRunner.Run(ipScript);
            if (!ipResult.Success)
            {
                report.Success = false;
                report.Message = "The previous IPv4 configuration could not be restored.";
                return report;
            }

            bool automaticDns = string.IsNullOrEmpty(LastBackup.PrimaryDns);
            PowerShellResult dnsResult = PowerShellRunner.Run(
                ProfileScripts.SetDns(adapter.InterfaceIndex, LastBackup.PrimaryDns, LastBackup.SecondaryDns, automaticDns));

            if (!dnsResult.Success)
            {
                report.Partial = true;
                report.Warnings.Add("The previous DNS configuration could not be restored.");
            }

            report.Success = true;
            report.Message = "Restored previous adapter configuration.";
            report.Steps.Add("Restored " + (LastBackup.IsDhcp ? "DHCP" : LastBackup.IpAddress));

            Log.Write("PROFILE-RESTORE", adapter.LogKey,
                LastBackup.IsDhcp ? "dhcp" : LastBackup.IpAddress, "previous state",
                report.Partial ? "partially restored" : "restored");

            return report;
        }
    }
}
