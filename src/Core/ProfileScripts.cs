using System;
using System.Text;

namespace VlanConfig95.Core
{
    /// <summary>
    /// PowerShell scripts for reading and writing the IPv4 / DNS / gateway part of an
    /// adapter's configuration.
    ///
    /// The existing VLAN implementation in PsScripts is reused unchanged; only the
    /// IP-level operations live here. Every user-supplied value is validated to a
    /// strict character set and passed as a quoted PowerShell literal, never
    /// concatenated into the command text.
    /// </summary>
    public static class ProfileScripts
    {
        /// <summary>
        /// Accepts only plain digits, dots and commas. This is the guard that keeps
        /// hostile profile values out of the generated script.
        /// </summary>
        public static bool IsSafeNumeric(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool ok = (c >= '0' && c <= '9') || c == '.' || c == ',';
                if (!ok) return false;
            }

            return true;
        }

        private static string Safe(string value, string fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            string trimmed = value.Trim();
            return IsSafeNumeric(trimmed) ? trimmed : fallback;
        }

        private static string Head()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("$records = @()");
            sb.AppendLine("try {");
            return sb.ToString();
        }

        private static string Tail()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("} catch {");
            // Name the step that failed - a bare CIM/parameter error is unreadable.
            sb.AppendLine("    $stepText = if ($null -ne $step) { ' (' + $step + ')' } else { '' }");
            sb.AppendLine("    Fail ($_.Exception.Message + $stepText)");
            sb.AppendLine("}");
            sb.AppendLine("Finish");
            return sb.ToString();
        }

        /// <summary>Reads the current IPv4, gateway, DHCP state and DNS servers.</summary>
        public static string ReadState(int interfaceIndex)
        {
            StringBuilder b = new StringBuilder(Head());
            b.AppendLine("    $idx = " + interfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.AppendLine("    $adapter = @(Get-NetAdapter -IncludeHidden -InterfaceIndex $idx -ErrorAction SilentlyContinue)");
            b.AppendLine("    if ($adapter.Count -eq 0) { throw ('The selected network adapter (interface ' + $idx + ') could not be located.') }");
            b.AppendLine("    $alias = [string]$adapter[0].Name");
            b.AppendLine("    $dhcp = 'Disabled'");
            b.AppendLine("    $ipif = Get-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -First 1");
            b.AppendLine("    if ($null -ne $ipif) { $dhcp = [string]$ipif.Dhcp }");
            b.AppendLine("    $ip = ''");
            b.AppendLine("    $prefix = ''");
            b.AppendLine("    $a = Get-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.PrefixOrigin -ne 'WellKnown' } | Select-Object -First 1");
            b.AppendLine("    if ($null -ne $a) { $ip = [string]$a.IPAddress; $prefix = [string]$a.PrefixLength }");
            b.AppendLine("    $gw = ''");
            b.AppendLine("    $r = Get-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Select-Object -First 1");
            b.AppendLine("    if ($null -ne $r) { $gw = [string]$r.NextHop }");
            b.AppendLine("    $dnsList = @()");
            b.AppendLine("    $d = Get-DnsClientServerAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue");
            b.AppendLine("    if ($null -ne $d) { $dnsList = @($d.ServerAddresses) }");
            b.AppendLine("    $dns1 = ''");
            b.AppendLine("    $dns2 = ''");
            b.AppendLine("    if ($dnsList.Count -ge 1) { $dns1 = [string]$dnsList[0] }");
            b.AppendLine("    if ($dnsList.Count -ge 2) { $dns2 = [string]$dnsList[1] }");
            b.AppendLine("    Emit 'S' @($idx,$alias,$dhcp,$ip,$prefix,$gw,$dns1,$dns2)");
            b.Append(Tail());
            return PsScripts.Preview(b.ToString());
        }

        /// <summary>Switches the adapter to DHCP and removes any static addresses.</summary>
        public static string SetDhcp(int interfaceIndex)
        {
            StringBuilder b = new StringBuilder(Head());
            b.AppendLine("    $step = 'locating the adapter'");
            b.AppendLine("    $idx = " + interfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.AppendLine("    $adapter = @(Get-NetAdapter -IncludeHidden -InterfaceIndex $idx -ErrorAction SilentlyContinue)");
            b.AppendLine("    if ($adapter.Count -eq 0) {");
            b.AppendLine("        for ($i = 0; $i -lt 20; $i++) {");
            b.AppendLine("            Start-Sleep -Milliseconds 500");
            b.AppendLine("            $adapter = @(Get-NetAdapter -IncludeHidden -InterfaceIndex $idx -ErrorAction SilentlyContinue)");
            b.AppendLine("            if ($adapter.Count -gt 0) { break }");
            b.AppendLine("        }");
            b.AppendLine("    }");
            b.AppendLine("    if ($adapter.Count -eq 0) { throw ('The selected network adapter (interface ' + $idx + ') could not be located.') }");
            b.AppendLine("    $step = 'removing the existing default route'");
            b.AppendLine("    Get-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue");
            AppendRemoveAllIpv4Addresses(b);
            b.AppendLine("    $step = 'enabling DHCP'");
            b.AppendLine("    Get-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction Stop | Set-NetIPInterface -Dhcp Enabled -ErrorAction Stop");
            b.AppendLine("    Emit 'OK' @('dhcp')");
            b.Append(Tail());
            return PsScripts.Preview(b.ToString());
        }

        /// <summary>
        /// Emits the PowerShell that removes every IPv4 address currently configured on
        /// the target interface, then returns.
        ///
        /// All IPv4 addresses are removed, not just the ones with a "manual" prefix
        /// origin. An APIPA (169.254.x.x) self-assigned address reports
        /// PrefixOrigin = 'WellKnown', so a filter such as
        /// "PrefixOrigin -ne 'WellKnown'" silently skipped it and left the adapter with
        /// both the old APIPA address and the new static one. A static profile is meant
        /// to be the only IPv4 configuration on the interface.
        ///
        /// Everything is scoped to -InterfaceIndex $idx, so addresses on other
        /// interfaces are never touched. The loop retries because the stack can hold an
        /// address for a moment after DHCP has been switched off.
        /// </summary>
        private static void AppendRemoveAllIpv4Addresses(StringBuilder b)
        {
            b.AppendLine("    $step = 'removing existing IPv4 addresses'");
            b.AppendLine("    for ($try = 0; $try -lt 6; $try++) {");
            b.AppendLine("        $left = @(Get-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue)");
            b.AppendLine("        if ($left.Count -eq 0) { break }");
            b.AppendLine("        foreach ($a in $left) {");
            // Removed one at a time, by exact address, so a single stubborn entry
            // cannot abort the rest.
            b.AppendLine("            Remove-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -IPAddress $a.IPAddress -Confirm:$false -ErrorAction SilentlyContinue");
            b.AppendLine("        }");
            b.AppendLine("        Start-Sleep -Milliseconds 400");
            b.AppendLine("    }");
        }

        /// <summary>Applies a static IPv4 address, prefix length and optional gateway.</summary>
        public static string SetStaticIp(int interfaceIndex, string ip, int prefix, string gateway)
        {
            string safeIp = Safe(ip, "0.0.0.0");
            string safeGw = string.IsNullOrEmpty(gateway) ? string.Empty : Safe(gateway, string.Empty);
            int safePrefix = Math.Max(0, Math.Min(32, prefix));

            StringBuilder b = new StringBuilder(Head());
            b.AppendLine("    $step = 'locating the adapter'");
            b.AppendLine("    $idx = " + interfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.AppendLine("    $adapter = @(Get-NetAdapter -IncludeHidden -InterfaceIndex $idx -ErrorAction SilentlyContinue)");
            b.AppendLine("    if ($adapter.Count -eq 0) {");
            b.AppendLine("        for ($i = 0; $i -lt 20; $i++) {");
            b.AppendLine("            Start-Sleep -Milliseconds 500");
            b.AppendLine("            $adapter = @(Get-NetAdapter -IncludeHidden -InterfaceIndex $idx -ErrorAction SilentlyContinue)");
            b.AppendLine("            if ($adapter.Count -gt 0) { break }");
            b.AppendLine("        }");
            b.AppendLine("    }");
            b.AppendLine("    if ($adapter.Count -eq 0) { throw ('The selected network adapter (interface ' + $idx + ') could not be located.') }");

            // DHCP MUST be switched off first. While it is still enabled the client
            // keeps re-acquiring its lease, so an address removed here is instantly
            // put back and the following New-NetIPAddress fails with
            // "Instance MSFT_NetIPAddress already exists".
            b.AppendLine("    $step = 'disabling DHCP'");
            b.AppendLine("    Get-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction Stop | Set-NetIPInterface -Dhcp Disabled -ErrorAction Stop");

            b.AppendLine("    $step = 'removing the existing default route'");
            b.AppendLine("    Get-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue");

            // Every existing IPv4 address, including any APIPA (169.254.x.x) address,
            // has to go before the profile address is added. Filtering on
            // PrefixOrigin skipped exactly those APIPA entries and produced a
            // dual-address adapter.
            AppendRemoveAllIpv4Addresses(b);

            b.AppendLine("    $step = 'setting the static IPv4 address'");
            // Clear the target address specifically: it may already be present with a
            // different prefix length, which New-NetIPAddress rejects outright.
            b.AppendLine("    Get-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -IPAddress " + PowerShellRunner.Quote(safeIp) + " -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue");
            b.AppendLine("    Start-Sleep -Milliseconds 300");
            b.AppendLine("    try {");
            b.AppendLine("        New-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -IPAddress " + PowerShellRunner.Quote(safeIp) + " -PrefixLength " + safePrefix + " -ErrorAction Stop | Out-Null");
            b.AppendLine("    } catch {");
            // Still occupied (the stack re-added it): update it in place instead.
            b.AppendLine("        $existing = @(Get-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -IPAddress " + PowerShellRunner.Quote(safeIp) + " -ErrorAction SilentlyContinue)");
            b.AppendLine("        if ($existing.Count -eq 0) { throw }");
            b.AppendLine("        Set-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -IPAddress " + PowerShellRunner.Quote(safeIp) + " -PrefixLength " + safePrefix + " -ErrorAction Stop | Out-Null");
            b.AppendLine("    }");
            if (safeGw.Length > 0)
            {
                b.AppendLine("    $step = 'setting the default gateway'");
                // Same "already exists" trap as the address: clear the default route
                // first, then fall back to updating it if the stack re-created it.
                b.AppendLine("    Get-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue");
                b.AppendLine("    Start-Sleep -Milliseconds 300");
                b.AppendLine("    try {");
                b.AppendLine("        New-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -NextHop " + PowerShellRunner.Quote(safeGw) + " -ErrorAction Stop | Out-Null");
                b.AppendLine("    } catch {");
                b.AppendLine("        $route = @(Get-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue)");
                b.AppendLine("        if ($route.Count -eq 0) { throw }");
                b.AppendLine("        Set-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -NextHop " + PowerShellRunner.Quote(safeGw) + " -ErrorAction Stop | Out-Null");
                b.AppendLine("    }");
            }
            b.AppendLine("    Emit 'OK' @('static')");
            b.Append(Tail());
            return PsScripts.Preview(b.ToString());
        }

        /// <summary>Sets static DNS servers, or restores them to automatic (DHCP).</summary>
        public static string SetDns(int interfaceIndex, string primary, string secondary, bool automatic)
        {
            StringBuilder b = new StringBuilder(Head());
            b.AppendLine("    $step = 'locating the adapter'");
            b.AppendLine("    $idx = " + interfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.AppendLine("    $adapter = @(Get-NetAdapter -IncludeHidden -InterfaceIndex $idx -ErrorAction SilentlyContinue)");
            b.AppendLine("    if ($adapter.Count -eq 0) {");
            b.AppendLine("        for ($i = 0; $i -lt 20; $i++) {");
            b.AppendLine("            Start-Sleep -Milliseconds 500");
            b.AppendLine("            $adapter = @(Get-NetAdapter -IncludeHidden -InterfaceIndex $idx -ErrorAction SilentlyContinue)");
            b.AppendLine("            if ($adapter.Count -gt 0) { break }");
            b.AppendLine("        }");
            b.AppendLine("    }");
            b.AppendLine("    if ($adapter.Count -eq 0) { throw ('The selected network adapter (interface ' + $idx + ') could not be located.') }");
            b.AppendLine("    if (" + (automatic ? "$true" : "$false") + ") {");
            b.AppendLine("        $step = 'resetting DNS to automatic'");
            b.AppendLine("        Set-DnsClientServerAddress -InterfaceIndex $idx -ResetServerAddresses -ErrorAction Stop");
            b.AppendLine("        Emit 'OK' @('dns-automatic')");
            b.AppendLine("    } else {");
            b.AppendLine("        $step = 'setting the DNS servers'");
            b.AppendLine("        $p = " + PowerShellRunner.Quote(Safe(primary, "0.0.0.0")));
            b.AppendLine("        $s = " + PowerShellRunner.Quote(Safe(secondary, string.Empty)));
            b.AppendLine("        $list = New-Object System.Collections.ArrayList");
            b.AppendLine("        [void]$list.Add($p)");
            b.AppendLine("        if ($s.Length -gt 0) { [void]$list.Add($s) }");
            // No -Validate switch: it is a per-server-validation hint that produces a
            // parameter-set error on some Windows builds and is not needed here.
            b.AppendLine("        Set-DnsClientServerAddress -InterfaceIndex $idx -ServerAddresses $list.ToArray() -ErrorAction Stop");
            b.AppendLine("        Emit 'OK' @('dns-static')");
            b.AppendLine("    }");
            b.Append(Tail());
            return PsScripts.Preview(b.ToString());
        }

        /// <summary>Waits for the adapter to come back after a driver reset.</summary>
        public static string WaitForAdapter(int interfaceIndex, int seconds)
        {
            int wait = Math.Max(2, Math.Min(30, seconds));

            StringBuilder b = new StringBuilder(Head());
            b.AppendLine("    $idx = " + interfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.AppendLine("    $deadline = (Get-Date).AddSeconds(" + wait + ")");
            b.AppendLine("    do {");
            b.AppendLine("        $a = @(Get-NetAdapter -IncludeHidden -InterfaceIndex $idx -ErrorAction SilentlyContinue)");
            b.AppendLine("        if ($a.Count -gt 0 -and $a[0].Status -ne 'Disconnected') { break }");
            b.AppendLine("        Start-Sleep -Milliseconds 500");
            b.AppendLine("    } while ((Get-Date) -lt $deadline)");
            b.AppendLine("    $a = @(Get-NetAdapter -IncludeHidden -InterfaceIndex $idx -ErrorAction SilentlyContinue)");
            b.AppendLine("    $st = 'Unknown'");
            b.AppendLine("    if ($a.Count -gt 0) { $st = [string]$a[0].Status }");
            b.AppendLine("    Emit 'W' @($st)");
            b.Append(Tail());
            return PsScripts.Preview(b.ToString());
        }
    }
}