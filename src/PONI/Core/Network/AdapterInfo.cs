using System;
using System.Collections.Generic;
using System.Linq;

namespace Poni.Core.Network
{
    public enum AdapterKind { Ethernet, WiFi, Virtual }

    public enum NetworkCategory { Unknown, Public, Private, Domain }

    public sealed class IpAddressInfo
    {
        public IpAddressInfo(string address, int prefixLength, bool isManual, bool isDuplicate = false)
        {
            Address = address;
            PrefixLength = prefixLength;
            IsManual = isManual;
            IsDuplicate = isDuplicate;
        }

        public string Address { get; }
        public int PrefixLength { get; }
        /// <summary>Typed by someone (not DHCP, not APIPA).</summary>
        public bool IsManual { get; }
        /// <summary>Windows detected another machine using this address.</summary>
        public bool IsDuplicate { get; }
        public bool IsApipa => Address.StartsWith("169.254.", StringComparison.Ordinal);
    }

    /// <summary>A host network adapter and its IPv4 configuration, as read from Windows.</summary>
    public sealed class AdapterInfo
    {
        public int InterfaceIndex { get; set; }
        /// <summary>Windows name, e.g. "Ethernet", "Wi-Fi" (can be renamed by the user).</summary>
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        /// <summary>Interface GUID "{...}" (registry key of its TCP/IP settings).</summary>
        public string? Guid { get; set; }
        public string? MacAddress { get; set; }
        public AdapterKind Kind { get; set; }
        public bool IsConnected { get; set; }
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// False when the adapter has no IPv4 stack: typically the physical port while it is the
        /// uplink of a Hyper-V external switch without host access (RJ45 given to a VM).
        /// </summary>
        public bool HasIpv4 { get; set; }
        public bool Dhcp { get; set; }
        public int InterfaceMetric { get; set; }
        public List<IpAddressInfo> Addresses { get; set; } = new List<IpAddressInfo>();
        /// <summary>Default gateway(s) (next hop of 0.0.0.0/0) with their route metric.</summary>
        public List<KeyValuePair<string, int>> Gateways { get; set; } = new List<KeyValuePair<string, int>>();
        /// <summary>DNS servers in use (static or from DHCP).</summary>
        public List<string> DnsServers { get; set; } = new List<string>();
        /// <summary>True when the DNS servers are typed in (not obtained automatically).</summary>
        public bool DnsIsStatic { get; set; }
        public NetworkCategory Category { get; set; }

        /// <summary>Set by the reader: this adapter carries the preferred default route (the "Internet" one).</summary>
        public bool IsInternetAdapter { get; set; }

        public bool IsVirtual => Kind == AdapterKind.Virtual;

        /// <summary>Main address shown to the user: a real IPv4 first, APIPA only as a last resort.</summary>
        public IpAddressInfo? PrimaryAddress =>
            Addresses.FirstOrDefault(a => !a.IsApipa) ?? Addresses.FirstOrDefault();

        public string? PrimaryGateway => Gateways.OrderBy(g => g.Value).Select(g => g.Key).FirstOrDefault();

        /// <summary>Has a routable IPv4 address (not only APIPA).</summary>
        public bool HasRealAddress => Addresses.Any(a => !a.IsApipa);
    }
}
