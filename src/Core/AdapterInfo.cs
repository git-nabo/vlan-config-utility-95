using System;

namespace VlanConfig95.Core
{
    public enum AdapterCategory
    {
        Ethernet,
        Wireless,
        Virtual,
        Vpn,
        HyperV,
        Bluetooth,
        Wwan,
        Loopback,
        Unknown
    }

    /// <summary>
    /// Immutable snapshot of one installed network adapter. Populated from
    /// Get-NetAdapter and cross-checked with the MSFT_NetAdapter CIM class.
    /// </summary>
    public sealed class AdapterInfo
    {
        public int InterfaceIndex;
        public string Name;
        public string Description;
        public string Status;
        public string MacAddress;
        public string LinkSpeed;
        public string MediaType;
        public string PhysicalMediaType;
        public string DriverProvider;
        public string DriverVersion;
        public string DriverFileName;
        public string DriverDate;
        public string ComponentId;
        public string PnpDeviceId;
        public string InterfaceGuid;
        public string NetCfgInstanceId;
        public string IPv4Addresses;
        public string IPv6Addresses;
        public bool IsVirtual;
        public AdapterCategory Category;
        public string CategoryLabel;

        /// <summary>CIM-reported VlanID (0 / empty means "not reported by driver").</summary>
        public int? CimVlanId;

        /// <summary>True when the adapter reports an Up operational status.</summary>
        public bool IsConnected
        {
            get
            {
                if (string.IsNullOrEmpty(Status))
                    return false;

                // "Up" in English; localised Windows uses the same enum string from CIM.
                return Status.Equals("Up", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// A stable identifier used for logging. Adapter names can be localised and may
        /// contain quotes, so the interface index is the reliable key.
        /// </summary>
        public string LogKey
        {
            get { return "ifIndex=" + InterfaceIndex + " name=\"" + Name + "\""; }
        }

        public AdapterInfo()
        {
            Name = string.Empty;
            Description = string.Empty;
            Status = string.Empty;
            MacAddress = string.Empty;
            LinkSpeed = string.Empty;
            MediaType = string.Empty;
            PhysicalMediaType = string.Empty;
            DriverProvider = string.Empty;
            DriverVersion = string.Empty;
            DriverFileName = string.Empty;
            DriverDate = string.Empty;
            ComponentId = string.Empty;
            PnpDeviceId = string.Empty;
            InterfaceGuid = string.Empty;
            NetCfgInstanceId = string.Empty;
            IPv4Addresses = string.Empty;
            IPv6Addresses = string.Empty;
            CategoryLabel = string.Empty;
        }
    }
}