using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using Microsoft.Win32;
using Poni.Core;
using Poni.Core.Network;

namespace Poni.Services.Network
{
    /// <summary>
    /// Reads the host network configuration through WMI (root\StandardCimv2, the classes behind
    /// Get-NetAdapter / Get-NetIPAddress / ...). Read-only, no admin rights needed, no PowerShell.
    /// Call from a background thread: a full read takes a few hundred milliseconds.
    /// </summary>
    public static class WmiNetworkReader
    {
        private const string Namespace = @"\\.\root\StandardCimv2";

        // NDIS physical medium 9 = native 802.11 (Wi-Fi). MediaConnectState 1 = connected.
        // InterfaceAdminStatus 1 = up. AddressFamily 2 = IPv4. PrefixOrigin 1 = manual.
        // AddressState 2 = duplicate. NetworkCategory 0 public, 1 private, 2 domain.

        public static List<AdapterInfo> ReadAdapters()
        {
            var scope = new ManagementScope(Namespace);
            scope.Connect();

            var adapters = new Dictionary<int, AdapterInfo>();
            foreach (var mo in Query(scope, "SELECT InterfaceIndex, Name, InterfaceDescription, DeviceID, PermanentAddress, Virtual, Hidden, NdisPhysicalMedium, MediaConnectState, InterfaceAdminStatus FROM MSFT_NetAdapter"))
            {
                if (AsBool(mo["Hidden"])) continue;
                var index = AsInt(mo["InterfaceIndex"]);
                bool isVirtual = AsBool(mo["Virtual"]);
                adapters[index] = new AdapterInfo
                {
                    InterfaceIndex = index,
                    Name = mo["Name"] as string ?? "",
                    Description = mo["InterfaceDescription"] as string ?? "",
                    Guid = mo["DeviceID"] as string,
                    MacAddress = mo["PermanentAddress"] as string, // "A1B2C3D4E5F6" (no separators)
                    Kind = isVirtual ? AdapterKind.Virtual : AsInt(mo["NdisPhysicalMedium"]) == 9 ? AdapterKind.WiFi : AdapterKind.Ethernet,
                    IsConnected = AsInt(mo["MediaConnectState"]) == 1,
                    IsEnabled = AsInt(mo["InterfaceAdminStatus"]) == 1,
                };
            }

            foreach (var mo in Query(scope, "SELECT InterfaceIndex, Dhcp, InterfaceMetric FROM MSFT_NetIPInterface WHERE AddressFamily = 2"))
            {
                if (!adapters.TryGetValue(AsInt(mo["InterfaceIndex"]), out var a)) continue;
                a.HasIpv4 = true;
                a.Dhcp = AsInt(mo["Dhcp"]) == 1;
                a.InterfaceMetric = AsInt(mo["InterfaceMetric"]);
            }

            foreach (var mo in Query(scope, "SELECT InterfaceIndex, IPAddress, PrefixLength, PrefixOrigin, AddressState FROM MSFT_NetIPAddress WHERE AddressFamily = 2"))
            {
                if (!adapters.TryGetValue(AsInt(mo["InterfaceIndex"]), out var a)) continue;
                var ip = mo["IPAddress"] as string;
                if (ip == null) continue;
                a.Addresses.Add(new IpAddressInfo(ip, AsInt(mo["PrefixLength"]), AsInt(mo["PrefixOrigin"]) == 1, AsInt(mo["AddressState"]) == 2));
            }

            foreach (var mo in Query(scope, "SELECT InterfaceIndex, NextHop, RouteMetric FROM MSFT_NetRoute WHERE DestinationPrefix = '0.0.0.0/0'"))
            {
                if (!adapters.TryGetValue(AsInt(mo["InterfaceIndex"]), out var a)) continue;
                var hop = mo["NextHop"] as string;
                if (string.IsNullOrEmpty(hop) || hop == "0.0.0.0") continue;
                a.Gateways.Add(new KeyValuePair<string, int>(hop!, AsInt(mo["RouteMetric"])));
            }

            foreach (var mo in Query(scope, "SELECT InterfaceIndex, ServerAddresses FROM MSFT_DNSClientServerAddress WHERE AddressFamily = 2"))
            {
                if (!adapters.TryGetValue(AsInt(mo["InterfaceIndex"]), out var a)) continue;
                if (mo["ServerAddresses"] is string[] servers) a.DnsServers = servers.Where(Ipv4.IsValid).ToList();
            }

            foreach (var mo in Query(scope, "SELECT InterfaceIndex, NetworkCategory FROM MSFT_NetConnectionProfile"))
            {
                if (!adapters.TryGetValue(AsInt(mo["InterfaceIndex"]), out var a)) continue;
                a.Category = AsInt(mo["NetworkCategory"]) switch
                {
                    0 => NetworkCategory.Public,
                    1 => NetworkCategory.Private,
                    2 => NetworkCategory.Domain,
                    _ => NetworkCategory.Unknown,
                };
            }

            foreach (var a in adapters.Values) a.DnsIsStatic = HasStaticDns(a.Guid);

            var list = adapters.Values
                .OrderBy(a => a.IsVirtual)
                .ThenByDescending(a => a.IsConnected)
                .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            NetworkRules.MarkInternetAdapter(list);
            return list;
        }

        /// <summary>Static DNS = the interface's "NameServer" registry value is set (DHCP ones live in "DhcpNameServer").</summary>
        private static bool HasStaticDns(string? guid)
        {
            if (string.IsNullOrEmpty(guid)) return false;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + guid);
                return key?.GetValue("NameServer") is string value && value.Trim().Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<ManagementBaseObject> Query(ManagementScope scope, string wql)
        {
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
            using var results = searcher.Get();
            foreach (ManagementBaseObject mo in results) yield return mo;
        }

        private static int AsInt(object? value)
        {
            try { return value == null ? 0 : Convert.ToInt32(value); }
            catch { return 0; }
        }

        private static bool AsBool(object? value) => value is bool b && b;
    }
}
