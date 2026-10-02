using System;
using System.Globalization;
using System.Net;

namespace VlanConfig95.Core
{
    public enum IpMode
    {
        Static = 0,
        Dhcp = 1
    }

    public enum DnsMode
    {
        Static = 0,
        Automatic = 1
    }

    /// <summary>
    /// A reusable network configuration that can be applied to any adapter.
    /// </summary>
    public sealed class NetworkProfile
    {
        public string Name;
        public string IpAddress;
        public string SubnetMask;
        public string PrefixLength;
        public string Gateway;
        public string PrimaryDns;
        public string SecondaryDns;
        public string VlanId;        // "" or "0" means untagged / none
        public string Description;

        public IpMode IpConfig = IpMode.Static;
        public DnsMode DnsConfig = DnsMode.Static;

        public NetworkProfile()
        {
            Name = string.Empty; IpAddress = string.Empty; SubnetMask = string.Empty;
            PrefixLength = string.Empty; Gateway = string.Empty; PrimaryDns = string.Empty;
            SecondaryDns = string.Empty; VlanId = string.Empty; Description = string.Empty;
        }

        public NetworkProfile Clone()
        {
            NetworkProfile p = new NetworkProfile();
            p.Name = Name; p.IpAddress = IpAddress; p.SubnetMask = SubnetMask;
            p.PrefixLength = PrefixLength; p.Gateway = Gateway; p.PrimaryDns = PrimaryDns;
            p.SecondaryDns = SecondaryDns; p.VlanId = VlanId; p.Description = Description;
            p.IpConfig = IpConfig; p.DnsConfig = DnsConfig;
            return p;
        }

        /// <summary>True when a VLAN ID is set (rather than untagged).</summary>
        public bool HasVlan
        {
            get
            {
                int v;
                return VlanInspector.TryInt(VlanId, out v) && v >= VlanInspector.MinVlanId;
            }
        }

        public int VlanValue
        {
            get
            {
                int v;
                return VlanInspector.TryInt(VlanId, out v) ? v : 0;
            }
        }

        /// <summary>Short one-line summary used in the main-window combo box.</summary>
        public string Summary
        {
            get
            {
                string ip = (IpConfig == IpMode.Dhcp) ? "DHCP" : IpAddress;
                string vlan = HasVlan ? ("VLAN " + VlanValue) : "no VLAN";
                return Name + "  (" + ip + ", " + vlan + ")";
            }
        }

        /// <summary>
        /// Validates the profile. Returns null when valid, otherwise a user-presentable
        /// message describing the first problem found. Nothing is applied unless this
        /// returns null, so invalid profiles can never be partially applied.
        /// </summary>
        public string Validate()
        {
            if (string.IsNullOrEmpty(Name) || Name.Trim().Length == 0)
                return "Enter a profile name.";

            if (IpConfig == IpMode.Static)
            {
                if (string.IsNullOrEmpty(IpAddress) || IpAddress.Trim().Length == 0)
                    return "Enter an IPv4 address, or choose DHCP.";

                if (!IsValidIPv4(IpAddress))
                    return "'" + IpAddress + "' is not a valid IPv4 address.";

                bool hasMask = !string.IsNullOrEmpty(SubnetMask) && SubnetMask.Trim().Length > 0;
                bool hasPrefix = !string.IsNullOrEmpty(PrefixLength) && PrefixLength.Trim().Length > 0;

                if (!hasMask && !hasPrefix)
                    return "Enter a subnet mask or a prefix length.";

                if (hasMask && !IsValidSubnetMask(SubnetMask))
                    return "'" + SubnetMask + "' is not a valid subnet mask.";

                if (hasPrefix)
                {
                    int prefix;
                    if (!VlanInspector.TryInt(PrefixLength, out prefix) || prefix < 0 || prefix > 32)
                        return "The prefix length must be between 0 and 32.";

                    if (hasMask && MaskToPrefix(SubnetMask) != prefix)
                        return "The subnet mask and prefix length do not describe the same subnet.";
                }

                if (string.IsNullOrEmpty(Gateway) || Gateway.Trim().Length == 0)
                    return "Enter a default gateway, or clear the field if the network has none.";
            }

            if (!string.IsNullOrEmpty(Gateway) && Gateway.Trim().Length > 0 && !IsValidIPv4(Gateway))
                return "'" + Gateway + "' is not a valid IPv4 address.";

            if (DnsConfig == DnsMode.Static)
            {
                if (string.IsNullOrEmpty(PrimaryDns) || PrimaryDns.Trim().Length == 0)
                    return "Enter a primary DNS server, or choose Automatic DNS.";

                if (!IsValidIPv4(PrimaryDns))
                    return "'" + PrimaryDns + "' is not a valid IPv4 address.";

                if (!string.IsNullOrEmpty(SecondaryDns) && SecondaryDns.Trim().Length > 0 &&
                    !IsValidIPv4(SecondaryDns))
                    return "'" + SecondaryDns + "' is not a valid IPv4 address.";
            }

            if (!string.IsNullOrEmpty(VlanId) && VlanId.Trim().Length > 0)
            {
                int vlan;
                if (!VlanInspector.TryInt(VlanId, out vlan))
                    return "'" + VlanId + "' is not a valid VLAN ID.";

                if (vlan != 0 && (vlan < VlanInspector.MinVlanId || vlan > VlanInspector.MaxVlanId))
                    return "The VLAN ID must be between " + VlanInspector.MinVlanId +
                           " and " + VlanInspector.MaxVlanId + " (or blank for none).";
            }

            return null;
        }

        public static bool IsValidIPv4(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            IPAddress parsed;
            if (!IPAddress.TryParse(text.Trim(), out parsed)) return false;

            // Reject shorthand forms such as "10.1" that IPAddress would otherwise accept.
            string[] parts = text.Trim().Split('.');
            if (parts.Length != 4) return false;

            foreach (string part in parts)
            {
                int value;
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value))
                    return false;
                if (value < 0 || value > 255) return false;
            }

            return true;
        }

        public static bool IsValidSubnetMask(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            string[] parts = text.Trim().Split('.');
            if (parts.Length != 4) return false;

            uint mask = 0;
            foreach (string part in parts)
            {
                int value;
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value))
                    return false;
                if (value < 0 || value > 255) return false;
                mask = (mask << 8) | (uint)value;
            }

            // A valid mask is a contiguous run of 1s followed by 0s.
            uint inverted = ~mask;
            return (inverted & (inverted + 1)) == 0;
        }

        public static int MaskToPrefix(string mask)
        {
            string[] parts = mask.Split('.');
            uint value = 0;
            foreach (string part in parts)
                value = (value << 8) | uint.Parse(part, CultureInfo.InvariantCulture);

            int count = 0;
            for (int i = 31; i >= 0; i--)
            {
                if ((value & (1u << i)) != 0) count++;
                else break;
            }

            return count;
        }

        /// <summary>Converts a prefix length into a dotted-quad mask.</summary>
        public static string PrefixToMask(int prefix)
        {
            if (prefix < 0) prefix = 0;
            if (prefix > 32) prefix = 32;

            uint value = prefix == 0 ? 0u : (uint)(0xFFFFFFFFu << (32 - prefix));

            return string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3}",
                (value >> 24) & 0xFF, (value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
        }
    }
}