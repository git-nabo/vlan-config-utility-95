using System;
using System.Text;

namespace VlanConfig95.Core
{
    /// <summary>
    /// Builds the PowerShell scripts used by the utility.
    ///
    /// Rules applied throughout:
    ///  * Adapter names are injected as escaped single-quoted literals.
    ///  * All payload text is escaped before leaving PowerShell.
    ///  * Nothing writes to the registry.
    /// </summary>
    public static class PsScripts
    {
        /// <summary>Escapes a value for the pipe-delimited transport format.</summary>
        public static string Esc(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value
                .Replace("^", "^^")
                .Replace("|", "^P")
                .Replace("\r", "^R")
                .Replace("\n", "^N");
        }

        /// <summary>Helper preamble prepended to every generated script.</summary>
        public const string Preamble =
            "$ErrorActionPreference = 'Stop'\n" +
            "$ProgressPreference = 'SilentlyContinue'\n" +
            "$WarningPreference = 'SilentlyContinue'\n" +
            // PowerShell writes redirected output in the console OEM codepage, which
            // mangles localised text (e.g. "PrioritÃƒÂ¤t"). Force UTF-8 so the
            // C# reader decodes it correctly.
            "try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }\n" +
            "function Esc([object]$v) {\n" +
            "    if ($null -eq $v) { return '' }\n" +
            "    $s = [string]$v\n" +
            "    $s = $s.Replace('^','^^').Replace('|','^P').Replace(\"`r\",'^R').Replace(\"`n\",'^N')\n" +
            "    return $s\n" +
            "}\n" +
            "$script:recs = New-Object System.Collections.ArrayList\n" +
            "function Emit([string]$kind, [object[]]$fields) {\n" +
            "    $parts = @($kind)\n" +
            "    foreach ($f in $fields) { $parts += (Esc $f) }\n" +
            "    [void]$script:recs.Add(($parts -join '|'))\n" +
            "}\n" +
            "function Finish() {\n" +
            "    $payload = ($script:recs -join \"`n\")\n" +
            "    Write-Output ('###VLANCFG:' + $payload + ':END###')\n" +
            "    exit 0\n" +
            "}\n" +
            "function Fail([string]$message) {\n" +
            "    $line = '!ERR|' + (Esc $message)\n" +
            "    Write-Output ('###VLANCFG:' + $line + ':END###')\n" +
            // Write to stdout only. Writing to stderr makes PowerShell emit CLIXML
            // when the stream is redirected, which corrupts the diagnostics.
            "    [Console]::Out.WriteLine('[error] ' + $message)\n" +
            "    exit 1\n" +
            "}\n";

        /// <summary>Enumerates all adapters, including hidden ones.</summary>
        public static string EnumerateAdapters()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Preamble);
            sb.Append(Environment.NewLine);
            sb.AppendLine("try {");
            sb.AppendLine("    $adapters = @(Get-NetAdapter -IncludeHidden)");
            sb.AppendLine("    $ip4 = @{}");
            sb.AppendLine("    $ip6 = @{}");
            sb.AppendLine("    foreach ($ip in @(Get-NetIPAddress)) {");
            sb.AppendLine("        $k = [string]$ip.InterfaceIndex");
            sb.AppendLine("        if ($ip.AddressFamily -eq 'IPv4') {");
            sb.AppendLine("            if (-not $ip4.ContainsKey($k)) { $ip4[$k] = @() }");
            sb.AppendLine("            $ip4[$k] += $ip.IPAddress");
            sb.AppendLine("        } elseif ($ip.AddressFamily -eq 'IPv6') {");
            sb.AppendLine("            if (-not $ip6.ContainsKey($k)) { $ip6[$k] = @() }");
            sb.AppendLine("            $ip6[$k] += $ip.IPAddress");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("    foreach ($a in $adapters) {");
            sb.AppendLine("        $k = [string]$a.ifIndex");
            sb.AppendLine("        $v4 = ''");
            sb.AppendLine("        $v6 = ''");
            sb.AppendLine("        if ($ip4.ContainsKey($k)) { $v4 = ($ip4[$k] -join ', ') }");
            sb.AppendLine("        if ($ip6.ContainsKey($k)) { $v6 = ($ip6[$k] -join ', ') }");
            sb.AppendLine("        Emit 'A' @($a.ifIndex,$a.Name,$a.InterfaceDescription,$a.Status,");
            sb.AppendLine("                     $a.MacAddress,$a.LinkSpeed,$a.MediaType,$a.NdisPhysicalMedium,");
            sb.AppendLine("                     $a.DriverProvider,$a.DriverVersion,$a.DriverFileName,$a.DriverDate,");
            sb.AppendLine("                     $a.ComponentID,$a.PnPDeviceID,$a.InterfaceGuid,$v4,$v6,$a.Virtual)");
            sb.AppendLine("    }");
            sb.AppendLine("} catch {");
            sb.AppendLine("    Fail ($_.Exception.Message)");
            sb.AppendLine("}");
            sb.AppendLine("Finish");
            return sb.ToString();
        }

        /// <summary>Produces a printable, runnable copy of a script for the Details window.</summary>
        public static string Preview(string body)
        {
            return Preamble + Environment.NewLine + body;
        }

        /// <summary>
        /// Reads every advanced property exposed by an adapter, plus the native CIM
        /// VlanID property. Raw values are returned untouched; all interpretation and
        /// VLAN-candidate scoring happens in C# (VlanInspector).
        ///
        /// The adapter is addressed by its interface index, never by its name.
        /// Get-NetAdapterAdvancedProperty has no -InterfaceIndex parameter, so the
        /// adapter object is piped in. This matters because adapter names may contain
        /// '*', '(' or ')', and -Name treats those as wildcards - which silently made
        /// adapters such as "LAN-Verbindung* 3" impossible to address.
        /// </summary>
        public static string ReadVlanInfo(int interfaceIndex)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Preamble);
            sb.Append(Environment.NewLine);
            sb.AppendLine("try {");
            sb.AppendLine("    $idx = " + interfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.AppendLine("    $adapter = @(Get-NetAdapter -IncludeHidden -InterfaceIndex $idx -ErrorAction Stop)[0]");
            sb.AppendLine("    $props = @()");
            sb.AppendLine("    try {");
            sb.AppendLine("        $props = @($adapter | Get-NetAdapterAdvancedProperty -AllProperties -ErrorAction Stop)");
            sb.AppendLine("    } catch {");
            sb.AppendLine("        $props = @()");
            sb.AppendLine("    }");
            sb.AppendLine("    if ($props.Count -eq 0) {");
            // Virtual switches, filters and some virtual NICs expose no advanced
            // properties at all. That means "no VLAN support", not an error.
            sb.AppendLine("        Emit 'C' @('')" );
            sb.AppendLine("        Finish");
            sb.AppendLine("    }");
            sb.AppendLine("    foreach ($p in $props) {");
            sb.AppendLine("        $rv = ''");
            sb.AppendLine("        if ($null -ne $p.RegistryValue) { $rv = ($p.RegistryValue -join ',') }");
            sb.AppendLine("        $vr = ''");
            sb.AppendLine("        if ($null -ne $p.ValidRegistryValues) { $vr = ($p.ValidRegistryValues -join ',') }");
            sb.AppendLine("        $vd = ''");
            sb.AppendLine("        if ($null -ne $p.ValidDisplayValues) { $vd = ($p.ValidDisplayValues -join ',') }");
            sb.AppendLine("        Emit 'P' @($p.RegistryKeyword,$p.DisplayName,$p.DisplayValue,$rv,");
            sb.AppendLine("                      $p.ValueType,$p.DisplayParameterType,$vr,$vd,");
            sb.AppendLine("                      $p.NumericParameterMinValue,$p.NumericParameterMaxValue,");
            sb.AppendLine("                      $p.DefaultRegistryValue,$p.DefaultDisplayValue)");
            sb.AppendLine("    }");
            sb.AppendLine("    $cimVlan = ''");
            sb.AppendLine("    $c = Get-CimInstance -Namespace root/StandardCimv2 -ClassName MSFT_NetAdapter -Filter \"InterfaceIndex=$idx\" -ErrorAction SilentlyContinue");
            sb.AppendLine("    if ($null -ne $c -and $null -ne $c.VlanID) { $cimVlan = [string]$c.VlanID }");
            sb.AppendLine("    Emit 'C' @($cimVlan)");
            sb.AppendLine("} catch {");
            sb.AppendLine("    Fail ($_.Exception.Message)");
            sb.AppendLine("}");
            sb.AppendLine("Finish");
            return sb.ToString();
        }

        /// <summary>
        /// Applies a VLAN ID through Set-NetAdapterAdvancedProperty using the driver's
        /// own RegistryKeyword. The value is validated to an integer before embedding.
        ///
        /// The adapter is resolved by interface index and piped in, so names containing
        /// '*', '(' or ')' cannot be mistaken for wildcards.
        /// </summary>
        public static string SetVlanProperty(int interfaceIndex, string registryKeyword, int vlanId, bool noRestart)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Preamble);
            sb.Append(Environment.NewLine);
            sb.AppendLine("try {");
            sb.AppendLine("    $idx = " + interfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.AppendLine("    $adapter = @(Get-NetAdapter -IncludeHidden -InterfaceIndex $idx -ErrorAction Stop)[0]");
            sb.AppendLine("    $adapter | Set-NetAdapterAdvancedProperty -RegistryKeyword " + PowerShellRunner.Quote(registryKeyword) +
                          " -RegistryValue " + vlanId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                          (noRestart ? " -NoRestart" : "") + " -ErrorAction Stop");
            sb.AppendLine("    Emit 'OK' @('applied')");
            sb.AppendLine("} catch {");
            sb.AppendLine("    Fail ($_.Exception.Message)");
            sb.AppendLine("}");
            sb.AppendLine("Finish");
            return sb.ToString();
        }

        /// <summary>Resets the VLAN property to the driver-reported default (normally 0).</summary>
        public static string ResetVlanProperty(int interfaceIndex, string registryKeyword, bool noRestart)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Preamble);
            sb.Append(Environment.NewLine);
            sb.AppendLine("try {");
            sb.AppendLine("    $idx = " + interfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.AppendLine("    $adapter = @(Get-NetAdapter -IncludeHidden -InterfaceIndex $idx -ErrorAction Stop)[0]");
            sb.AppendLine("    $adapter | Set-NetAdapterAdvancedProperty -RegistryKeyword " + PowerShellRunner.Quote(registryKeyword) +
                          " -RegistryValue 0" + (noRestart ? " -NoRestart" : "") + " -ErrorAction Stop");
            sb.AppendLine("    Emit 'OK' @('reset')");
            sb.AppendLine("} catch {");
            sb.AppendLine("    Fail ($_.Exception.Message)");
            sb.AppendLine("}");
            sb.AppendLine("Finish");
            return sb.ToString();
        }

        /// <summary>
        /// Restarts an adapter so a pending advanced-property change takes effect.
        /// Used only when explicitly requested by the user.
        /// </summary>
        public static string RestartAdapter(int interfaceIndex)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Preamble);
            sb.Append(Environment.NewLine);
            sb.AppendLine("try {");
            sb.AppendLine("    $idx = " + interfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.AppendLine("    Restart-NetAdapter -InterfaceIndex $idx -ErrorAction Stop");
            sb.AppendLine("    Emit 'OK' @('restarted')");
            sb.AppendLine("} catch {");
            sb.AppendLine("    Fail ($_.Exception.Message)");
            sb.AppendLine("}");
            sb.AppendLine("Finish");
            return sb.ToString();
        }
    }
}