using System.Collections.Generic;
using Poni.Core;
using Poni.Core.Network;
using Xunit;

namespace Poni.Tests
{
    public class NetworkRulesTests
    {
        private static AdapterInfo Static(string ip = "192.168.1.220", int prefix = 24, string? gateway = "192.168.1.1",
                                          string[]? dns = null, bool dnsStatic = true)
        {
            var a = new AdapterInfo
            {
                Name = "Ethernet", InterfaceIndex = 28, HasIpv4 = true, IsConnected = true, Dhcp = false,
                Addresses = { new IpAddressInfo(ip, prefix, isManual: true) },
                DnsServers = new List<string>(dns ?? new[] { "1.1.1.1" }),
                DnsIsStatic = dnsStatic,
            };
            if (gateway != null) a.Gateways.Add(new KeyValuePair<string, int>(gateway, 0));
            return a;
        }

        private static NetworkProfile Profile(string? gateway = "192.168.1.1", params string[] dns) => new NetworkProfile
        {
            Name = "Bureau", IPAddress = "192.168.1.220", PrefixLength = 24, Gateway = gateway,
            Dns = new List<string>(dns.Length == 0 ? new[] { "1.1.1.1" } : dns),
        };

        [Fact]
        public void Profile_is_active_only_on_an_exact_static_match()
        {
            Assert.True(NetworkRules.Matches(Profile(), Static()));
            Assert.False(NetworkRules.Matches(Profile(), Static(ip: "192.168.1.221")));
            Assert.False(NetworkRules.Matches(Profile(), Static(prefix: 16)));
            Assert.False(NetworkRules.Matches(Profile(), Static(gateway: "192.168.1.254")));
            Assert.False(NetworkRules.Matches(Profile(gateway: null), Static()));
            Assert.False(NetworkRules.Matches(Profile(), Static(dns: new[] { "8.8.8.8" })));
            // DHCP-provided DNS never count as the profile's static DNS.
            Assert.False(NetworkRules.Matches(Profile(), Static(dnsStatic: false)));
        }

        [Fact]
        public void Dns_order_does_not_matter_but_the_set_does()
        {
            Assert.True(NetworkRules.Matches(Profile("192.168.1.1", "1.1.1.1", "8.8.8.8"), Static(dns: new[] { "8.8.8.8", "1.1.1.1" })));
            Assert.False(NetworkRules.Matches(Profile("192.168.1.1", "1.1.1.1", "8.8.8.8"), Static(dns: new[] { "1.1.1.1" })));
        }

        [Fact]
        public void A_dhcp_adapter_never_matches_a_profile()
        {
            var a = Static();
            a.Dhcp = true;
            Assert.False(NetworkRules.Matches(Profile(), a));
        }

        [Fact]
        public void Snapshot_of_a_static_adapter_keeps_manual_addresses_gateway_and_static_dns()
        {
            var a = Static(dns: new[] { "1.1.1.1", "9.9.9.9" });
            a.Addresses.Add(new IpAddressInfo("169.254.3.4", 16, isManual: false)); // APIPA: not restored
            var snap = Ipv4Config.SnapshotOf(a);
            Assert.False(snap.UseDhcp);
            Assert.Equal(new[] { "192.168.1.220/24" }, snap.Addresses);
            Assert.Equal("192.168.1.1", snap.Gateway);
            Assert.Equal(new[] { "1.1.1.1", "9.9.9.9" }, snap.Dns);
            Assert.True(snap.IsRestorable);
        }

        [Fact]
        public void Snapshot_keeps_dhcp_and_ignores_dhcp_dns()
        {
            var a = Static(dnsStatic: false);
            a.Dhcp = true;
            Assert.True(Ipv4Config.SnapshotOf(a).UseDhcp);

            var noDns = Ipv4Config.SnapshotOf(Static(dnsStatic: false));
            Assert.Empty(noDns.Dns); // restore = automatic DNS, not the DHCP servers frozen as static
        }

        [Fact]
        public void Snapshot_without_address_is_not_restorable_as_such()
        {
            var a = new AdapterInfo { HasIpv4 = true, Dhcp = false };
            Assert.False(Ipv4Config.SnapshotOf(a).IsRestorable);
        }

        [Fact]
        public void Profile_config_carries_ip_prefix_gateway_and_dns()
        {
            var c = Ipv4Config.FromProfile(Profile("192.168.1.1", "8.8.8.8"));
            Assert.False(c.UseDhcp);
            Assert.Equal(new[] { "192.168.1.220/24" }, c.Addresses);
            Assert.Equal("192.168.1.1", c.Gateway);
            Assert.Equal(new[] { "8.8.8.8" }, c.Dns);
            Assert.Null(Ipv4Config.FromProfile(Profile(gateway: "")).Gateway);
        }

        [Fact]
        public void Internet_adapter_is_the_connected_one_with_the_best_route()
        {
            var wifi = Static(ip: "172.16.5.140", gateway: "172.16.5.169");
            wifi.Name = "Wi-Fi"; wifi.InterfaceMetric = 50; wifi.Gateways[0] = new KeyValuePair<string, int>("172.16.5.169", 0);
            var eth = Static(ip: "10.20.30.230", gateway: "10.20.30.254");
            eth.InterfaceMetric = 5; eth.Gateways[0] = new KeyValuePair<string, int>("10.20.30.254", 256);
            eth.IsConnected = false; // cable unplugged: its route does not count
            var list = new List<AdapterInfo> { eth, wifi };

            NetworkRules.MarkInternetAdapter(list);
            Assert.True(wifi.IsInternetAdapter);
            Assert.False(eth.IsInternetAdapter);

            eth.IsConnected = true; // 5 + 256 = 261 > 50 + 0
            NetworkRules.MarkInternetAdapter(list);
            Assert.True(wifi.IsInternetAdapter);
        }

        [Fact]
        public void Apply_blockers_and_risk()
        {
            var noStack = Static();
            noStack.HasIpv4 = false; // RJ45 given to a VM
            Assert.Equal(ApplyBlocker.NoIpv4, NetworkRules.CanApply(noStack));

            var disabled = Static();
            disabled.IsEnabled = false;
            Assert.Equal(ApplyBlocker.Disabled, NetworkRules.CanApply(disabled));

            Assert.Equal(ApplyBlocker.None, NetworkRules.CanApply(Static()));

            var wifi = Static();
            wifi.Kind = AdapterKind.WiFi;
            Assert.True(NetworkRules.IsRisky(wifi));
            var internet = Static();
            internet.IsInternetAdapter = true;
            Assert.True(NetworkRules.IsRisky(internet));
            Assert.False(NetworkRules.IsRisky(Static()));
        }

        [Fact]
        public void Virtual_adapters_are_listed_only_with_a_real_address()
        {
            var apipaOnly = new AdapterInfo { Kind = AdapterKind.Virtual, Addresses = { new IpAddressInfo("169.254.1.2", 16, false) } };
            var real = new AdapterInfo { Kind = AdapterKind.Virtual, Addresses = { new IpAddressInfo("172.17.80.1", 20, true) } };
            Assert.False(NetworkRules.IsWorthListing(apipaOnly));
            Assert.True(NetworkRules.IsWorthListing(real));
            Assert.False(NetworkRules.IsShownByDefault(real));
            Assert.True(NetworkRules.IsShownByDefault(new AdapterInfo { Kind = AdapterKind.Ethernet }));
        }
    }
}
