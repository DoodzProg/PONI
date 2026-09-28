using System;
using System.IO;
using System.Linq;
using System.Management.Automation.Language;
using Poni.Core;
using Poni.Core.HyperV;
using Poni.Core.Network;
using Xunit;

namespace Poni.Tests
{
    /// <summary>
    /// Hyper-V logic. SAFETY: nothing here touches Hyper-V. The state comes from a
    /// fixture (fictitious VM names); the scripts are only PARSED, never run.
    /// </summary>
    public class HyperVTests
    {
        private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", name));

        private static HyperVState Sample() => Rj45Rules.ParseState(Fixture("hyperv-state.json"));

        [Fact]
        public void Parses_the_state_written_by_the_script()
        {
            var s = Sample();
            Assert.Equal(new[] { "VM-Prod", "VM-Test", "Win11-Template" }, s.Vms.Select(v => v.Name));
            Assert.True(s.FindVm("VM-Test")!.IsRunning);
            Assert.False(s.FindVm("VM-Prod")!.IsRunning);
            var rj = s.AdaptersOf("VM-Test").Single(a => a.Name == "RJ45-Adapter");
            Assert.Equal("RJ45-Switch", rj.SwitchName);
            Assert.Equal("00155D38010B", rj.Mac);
            Assert.False(s.AdaptersOf("VM-Prod").Single(a => a.Name == "Carte-RJ45").HasUsableMac);
            var sw = s.Switches.Single(x => x.Name == "RJ45-Switch");
            Assert.Equal("External", sw.Type);
            Assert.False(sw.HostAccess);
        }

        [Fact]
        public void Single_items_unwrapped_by_PowerShell_are_still_read()
        {
            var s = Rj45Rules.ParseState(@"{""Vms"":{""Name"":""Solo"",""State"":""Off""},""Adapters"":[],""Switches"":{""Name"":""Default Switch"",""Type"":""Internal"",""Uplink"":"""",""HostAccess"":true}}");
            Assert.Equal("Solo", s.Vms.Single().Name);
            Assert.Single(s.Switches);
        }

        [Fact]
        public void Rj45_mode_comes_from_the_live_state()
        {
            var s = Sample();
            var status = Rj45Rules.Compute(s);
            Assert.Equal(Rj45Mode.Vm, status.Mode);
            Assert.Equal("VM-Test", status.CurrentVm);

            // No RJ45 switch: the port belongs to Windows (whatever was remembered before).
            s.Switches.RemoveAll(x => x.Name == "RJ45-Switch");
            Assert.Equal(Rj45Mode.Host, Rj45Rules.Compute(s).Mode);
        }

        [Fact]
        public void Two_vms_on_the_port_is_detected_as_shared()
        {
            var s = Sample();
            s.AdaptersOf("Win11-Template").First().SwitchName = "RJ45-Switch"; // what v1 could leave behind
            var status = Rj45Rules.Compute(s);
            Assert.Equal(Rj45Mode.Shared, status.Mode);
            Assert.Equal(2, status.ConnectedVms.Count);
            Assert.Null(status.CurrentVm);
        }

        [Fact]
        public void Switch_without_any_vm_is_an_orphan()
        {
            var s = Sample();
            foreach (var a in s.Adapters.Where(a => a.SwitchName == "RJ45-Switch")) a.SwitchName = "";
            Assert.Equal(Rj45Mode.Orphan, Rj45Rules.Compute(s).Mode);
        }

        [Fact]
        public void A_users_own_switch_with_a_similar_name_is_never_the_rj45_switch()
        {
            var s = Sample();
            s.Switches.RemoveAll(x => x.Name == "RJ45-Switch");
            s.Switches.Add(new SwitchInfo { Name = "Switch_RJ45", Type = "Internal" }); // a user's own switch with a look-alike name
            foreach (var a in s.Adapters.Where(a => a.SwitchName == "RJ45-Switch")) a.SwitchName = "Switch_RJ45";
            Assert.Equal(Rj45Mode.Host, Rj45Rules.Compute(s).Mode);
        }

        [Fact]
        public void Physical_adapter_choice_is_never_hard_coded()
        {
            var eth2 = new AdapterInfo { Name = "Ethernet 2", Kind = AdapterKind.Ethernet };
            var eth = new AdapterInfo { Name = "Ethernet", Kind = AdapterKind.Ethernet };
            var wifi = new AdapterInfo { Name = "Wi-Fi", Kind = AdapterKind.WiFi };
            var vnic = new AdapterInfo { Name = "vEthernet (Default Switch)", Kind = AdapterKind.Virtual };
            var all = new[] { wifi, vnic, eth2, eth };

            Assert.Same(eth2, Rj45Rules.PickPhysicalAdapter(all, "Ethernet 2"));      // the user's choice
            Assert.Same(eth, Rj45Rules.PickPhysicalAdapter(all, null));                 // "Ethernet" by default
            Assert.Same(eth, Rj45Rules.PickPhysicalAdapter(all, "Removed dock"));       // stale choice -> default
            Assert.Same(eth2, Rj45Rules.PickPhysicalAdapter(new[] { wifi, eth2 }, null)); // first wired one
            Assert.Null(Rj45Rules.PickPhysicalAdapter(new[] { wifi, vnic }, "Wi-Fi"));    // never Wi-Fi / virtual
        }

        [Fact]
        public void Uplink_adapter_is_found_by_its_description()
        {
            var status = Rj45Rules.Compute(Sample());
            var eth = new AdapterInfo { Name = "Ethernet", Description = "Intel(R) Ethernet Connection (22) I219-LM" };
            var wifi = new AdapterInfo { Name = "Wi-Fi", Description = "Intel(R) Wi-Fi 6E AX211 160MHz" };
            Assert.Same(eth, Rj45Rules.UplinkOf(status, new[] { wifi, eth }));
        }

        [Fact]
        public void Preferred_vm_adapter_is_the_last_used_then_the_rj45_one()
        {
            var adapters = Sample().AdaptersOf("VM-Test").ToList();
            Assert.Equal("RJ45-Adapter", Rj45Rules.PreferredVmAdapter(adapters, null)!.Name);
            Assert.Equal("Carte réseau", Rj45Rules.PreferredVmAdapter(adapters, "Carte réseau")!.Name);
        }

        [Fact]
        public void Adapters_pointing_to_a_deleted_switch_are_detected()
        {
            // Real state of 2026-09-28: Connected = True with an empty SwitchName on every Carte-RJ45.
            var s = Rj45Rules.ParseState(@"{""Vms"":[{""Name"":""A"",""State"":""Off""}],""Adapters"":[
                {""VMName"":""A"",""Name"":""Carte-RJ45"",""SwitchName"":"""",""Mac"":""000000000000"",""Connected"":true},
                {""VMName"":""A"",""Name"":""Unplugged"",""SwitchName"":"""",""Mac"":""00155D000001"",""Connected"":false},
                {""VMName"":""A"",""Name"":""Dongle"",""SwitchName"":""Dongle-Switch"",""Mac"":""00155D000002"",""Connected"":true}],""Switches"":[]}");
            var dangling = Assert.Single(s.DanglingAdapters);
            Assert.Equal("Carte-RJ45", dangling.Name);
        }

        [Fact]
        public void Giving_the_port_back_unplugs_the_vms_before_removing_the_switch()
        {
            // Removing the switch first left dangling adapters: those VMs could not start any more.
            using var stream = typeof(Poni.Services.PowerShellHost).Assembly.GetManifestResourceStream("Poni.Scripts.Set-Rj45ToHost.ps1")!;
            using var reader = new StreamReader(stream);
            var ast = Parser.ParseInput(reader.ReadToEnd(), out _, out _);
            int Position(string command) => ast.FindAll(n => n is CommandAst c && c.GetCommandName() == command, true)
                                               .Cast<CommandAst>().Select(c => c.Extent.StartOffset).DefaultIfEmpty(-1).Min();
            var disconnect = Position("Disconnect-VMNetworkAdapter");
            var remove = Position("Remove-VMSwitch");
            Assert.True(disconnect >= 0 && remove >= 0 && disconnect < remove);
        }

        [Fact]
        public void Repair_script_only_unplugs_adapters()
        {
            using var stream = typeof(Poni.Services.PowerShellHost).Assembly.GetManifestResourceStream("Poni.Scripts.Repair-VmAdapters.ps1")!;
            using var reader = new StreamReader(stream);
            var ast = Parser.ParseInput(reader.ReadToEnd(), out _, out var errors);
            Assert.Empty(errors);
            var commands = ast.FindAll(n => n is CommandAst, true).Cast<CommandAst>().Select(c => c.GetCommandName()).ToList();
            Assert.Contains("Disconnect-VMNetworkAdapter", commands);
            Assert.DoesNotContain(commands, c => c != null && (c.StartsWith("Remove-") || c.StartsWith("New-VMSwitch") || c.StartsWith("Set-VMSwitch")
                                                               || c.StartsWith("Connect-") || c.StartsWith("Start-VM") || c.StartsWith("Stop-VM")));
        }

        [Fact]
        public void Vm_ping_only_ever_touches_ponis_own_firewall_rule()
        {
            // Allowing ping inside a VM must never enable, change or remove a Windows (or user) rule.
            using var stream = typeof(Poni.Services.PowerShellHost).Assembly.GetManifestResourceStream("Poni.Scripts.Invoke-VmIpv4.ps1")!;
            using var reader = new StreamReader(stream);
            var ast = Parser.ParseInput(reader.ReadToEnd(), out _, out var errors);
            Assert.Empty(errors);
            var firewall = ast.FindAll(n => n is CommandAst c && (c.GetCommandName() ?? "").EndsWith("-NetFirewallRule"), true).Cast<CommandAst>().ToList();
            Assert.Contains(firewall, c => c.GetCommandName() == "New-NetFirewallRule");
            foreach (var c in firewall)
            {
                var elements = c.CommandElements;
                var nameIndex = elements.ToList().FindIndex(e => e is CommandParameterAst p && p.ParameterName == "Name");
                Assert.True(nameIndex > 0, c.Extent.Text);
                var value = Assert.IsType<StringConstantExpressionAst>(elements[nameIndex + 1]);
                Assert.Equal("PONI-Ping-In", value.Value);
                Assert.DoesNotContain(elements, e => e is CommandParameterAst p && (p.ParameterName == "DisplayGroup" || p.ParameterName == "Group" || p.ParameterName == "All"));
                Assert.DoesNotContain(c.GetCommandName(), new[] { "Set-NetFirewallRule", "Enable-NetFirewallRule", "Disable-NetFirewallRule" });
            }
            // Ping rule limited to the local network, echo requests only.
            var create = firewall.Single(c => c.GetCommandName() == "New-NetFirewallRule").Extent.Text;
            Assert.Contains("-RemoteAddress LocalSubnet", create);
            Assert.Contains("-IcmpType 8", create);
        }

        [Theory]
        [InlineData("00-15-5D-38-01-0A")]
        [InlineData("00:15:5d:38:01:0a")]
        [InlineData(" 00155D38010A ")]
        public void Mac_addresses_are_normalized(string mac)
        {
            Assert.Equal("00155D38010A", Rj45Rules.NormalizeMac(mac));
        }

        [Fact]
        public void Remembered_vm_users_round_trip()
        {
            var data = new StoreData();
            data.Settings.VmUsers["VM-Test"] = "admin";
            var back = StoreSerializer.Deserialize(StoreSerializer.Serialize(data), out _);
            Assert.Equal("admin", back.Settings.VmUsers["vm-test"]); // lookup is case-insensitive
        }

        /// <summary>
        /// HARD SAFEGUARD, checked on the script text: the only switch PONI may ever remove or create is
        /// 'RJ45-Switch' written literally (never a variable, never a parameter) - a user's permanent
        /// switch (e.g. a licence link between host and VM) must never be touched.
        /// </summary>
        [Theory]
        [InlineData("Poni.Scripts.Set-Rj45ToHost.ps1", "Remove-VMSwitch")]
        [InlineData("Poni.Scripts.Set-Rj45ToVm.ps1", "New-VMSwitch")]
        public void Switch_changes_only_ever_target_RJ45_Switch(string resource, string command)
        {
            using var stream = typeof(Poni.Services.PowerShellHost).Assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var ast = Parser.ParseInput(reader.ReadToEnd(), out _, out var errors);
            Assert.Empty(errors);

            var calls = ast.FindAll(n => n is CommandAst c && string.Equals(c.GetCommandName(), command, StringComparison.OrdinalIgnoreCase), true)
                           .Cast<CommandAst>().ToList();
            Assert.NotEmpty(calls);
            foreach (var call in calls)
            {
                var elements = call.CommandElements.ToList();
                var nameIndex = elements.FindIndex(e => e is CommandParameterAst p && p.ParameterName.Equals("Name", StringComparison.OrdinalIgnoreCase));
                Assert.True(nameIndex >= 0, command + " must name its switch explicitly");
                var value = elements[nameIndex + 1];
                var literal = value as StringConstantExpressionAst;
                var variable = value as VariableExpressionAst;
                // New-VMSwitch uses $SwitchName, itself assigned the literal 'RJ45-Switch' in the script.
                if (variable != null)
                {
                    var assigned = ast.FindAll(n => n is AssignmentStatementAst a && a.Left is VariableExpressionAst v
                                                   && v.VariablePath.UserPath == variable.VariablePath.UserPath, true)
                                      .Cast<AssignmentStatementAst>().ToList();
                    Assert.Single(assigned);
                    Assert.Equal("'RJ45-Switch'", assigned[0].Right.Extent.Text);
                    var paramBlock = ast.ParamBlock?.Parameters.Select(p => p.Name.VariablePath.UserPath) ?? Enumerable.Empty<string>();
                    Assert.DoesNotContain(variable.VariablePath.UserPath, paramBlock);
                }
                else
                {
                    Assert.NotNull(literal);
                    Assert.Equal("RJ45-Switch", literal!.Value);
                }
            }
        }
    }
}
