using System;
using System.Collections.Generic;
using System.Linq;

namespace Poni.Core.Network
{
    /// <summary>An IPv4 configuration to set on an adapter: either DHCP, or static values.</summary>
    public sealed class Ipv4Config
    {
        public bool UseDhcp { get; set; }
        /// <summary>Static addresses as "ip/prefix" (the first one is the main address).</summary>
        public List<string> Addresses { get; set; } = new List<string>();
        public string? Gateway { get; set; }
        /// <summary>Static DNS servers; empty = automatic.</summary>
        public List<string> Dns { get; set; } = new List<string>();

        public static Ipv4Config FromProfile(NetworkProfile p) => new Ipv4Config
        {
            UseDhcp = false,
            Addresses = { p.IPAddress + "/" + p.PrefixLength },
            Gateway = string.IsNullOrEmpty(p.Gateway) ? null : p.Gateway,
            Dns = new List<string>(p.Dns),
        };

        public static Ipv4Config Dhcp() => new Ipv4Config { UseDhcp = true };

        /// <summary>
        /// What to put back if applying fails: the adapter's current configuration.
        /// DHCP stays DHCP; otherwise its typed-in addresses, gateway and static DNS.
        /// </summary>
        public static Ipv4Config SnapshotOf(AdapterInfo adapter)
        {
            if (adapter.Dhcp) return Dhcp();
            return new Ipv4Config
            {
                UseDhcp = false,
                Addresses = adapter.Addresses.Where(a => a.IsManual).Select(a => a.Address + "/" + a.PrefixLength).ToList(),
                Gateway = adapter.PrimaryGateway,
                Dns = adapter.DnsIsStatic ? new List<string>(adapter.DnsServers) : new List<string>(),
            };
        }

        /// <summary>A static snapshot without any address cannot be restored as such: fall back to DHCP.</summary>
        public bool IsRestorable => UseDhcp || Addresses.Count > 0;
    }

    /// <summary>Why an adapter cannot receive a profile right now.</summary>
    public enum ApplyBlocker { None, NoIpv4, Disabled }

    public static class NetworkRules
    {
        public static ApplyBlocker CanApply(AdapterInfo adapter)
        {
            if (!adapter.IsEnabled) return ApplyBlocker.Disabled;
            if (!adapter.HasIpv4) return ApplyBlocker.NoIpv4;
            return ApplyBlocker.None;
        }

        /// <summary>
        /// Reconfiguring this adapter may cut the user off: it carries the Internet route,
        /// or it is Wi-Fi (usually DHCP, often 802.1X at work).
        /// </summary>
        public static bool IsRisky(AdapterInfo adapter) => adapter.IsInternetAdapter || adapter.Kind == AdapterKind.WiFi;

        /// <summary>
        /// The profile is what the adapter currently runs: same static address and prefix, same
        /// gateway, and same static DNS list (order ignored).
        /// </summary>
        public static bool Matches(NetworkProfile profile, AdapterInfo adapter)
        {
            if (!adapter.HasIpv4 || adapter.Dhcp) return false;
            if (!adapter.Addresses.Any(a => a.IsManual && a.Address == profile.IPAddress && a.PrefixLength == profile.PrefixLength))
                return false;

            var gateway = string.IsNullOrEmpty(profile.Gateway) ? null : profile.Gateway;
            if (!string.Equals(gateway, adapter.PrimaryGateway, StringComparison.Ordinal)) return false;

            var wanted = new HashSet<string>(profile.Dns);
            var actual = adapter.DnsIsStatic ? new HashSet<string>(adapter.DnsServers) : new HashSet<string>();
            return wanted.SetEquals(actual);
        }

        /// <summary>Marks the adapter holding the best default route (lowest interface + route metric).</summary>
        public static void MarkInternetAdapter(IList<AdapterInfo> adapters)
        {
            AdapterInfo? best = null;
            int bestMetric = int.MaxValue;
            foreach (var adapter in adapters)
            {
                adapter.IsInternetAdapter = false;
                if (!adapter.IsConnected || adapter.Gateways.Count == 0) continue;
                int metric = adapter.InterfaceMetric + adapter.Gateways.Min(g => g.Value);
                if (metric < bestMetric)
                {
                    bestMetric = metric;
                    best = adapter;
                }
            }
            if (best != null) best.IsInternetAdapter = true;
        }

        /// <summary>
        /// Adapters worth showing: physical ones always; virtual ones only when they carry a real
        /// address (VirtualBox, Hyper-V vEthernet...), unless the caller asks for all.
        /// </summary>
        public static bool IsShownByDefault(AdapterInfo adapter) => !adapter.IsVirtual;

        public static bool IsWorthListing(AdapterInfo adapter) => !adapter.IsVirtual || adapter.HasRealAddress;
    }
}
