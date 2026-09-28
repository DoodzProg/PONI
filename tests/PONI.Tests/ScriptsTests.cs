using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation.Language;
using System.Threading.Tasks;
using Poni.Services;
using Poni.Services.Network;
using Xunit;

namespace Poni.Tests
{
    /// <summary>
    /// The PowerShell scripts embedded in PONI.exe, and the in-process PowerShell host.
    /// SAFETY: nothing here changes the network. The only executions target an interface index
    /// that does not exist, which the scripts must detect before touching anything.
    /// </summary>
    public class ScriptsTests
    {
        private const int MissingInterface = 987654;

        public static IEnumerable<object[]> EmbeddedScripts() =>
            typeof(PowerShellHost).Assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith("Poni.Scripts.") && n.EndsWith(".ps1"))
                .Select(n => new object[] { n });

        private static string Read(string resource)
        {
            using var stream = typeof(PowerShellHost).Assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        [Fact]
        public void Expected_scripts_are_embedded()
        {
            var names = EmbeddedScripts().Select(o => (string)o[0]).ToList();
            Assert.Contains("Poni.Scripts.Set-Ipv4Config.ps1", names);
            Assert.Contains("Poni.Scripts.Set-NetworkCategory.ps1", names);
        }

        [Theory]
        [MemberData(nameof(EmbeddedScripts))]
        public void Embedded_scripts_have_no_syntax_error(string resource)
        {
            Parser.ParseInput(Read(resource), out _, out var errors);
            Assert.Empty(errors);
        }

        [Theory]
        [MemberData(nameof(EmbeddedScripts))]
        public void Embedded_scripts_never_use_the_PROFILE_automatic_variable(string resource)
        {
            // $profile is PowerShell's $PROFILE (case-insensitive): a classic trap.
            var ast = Parser.ParseInput(Read(resource), out _, out _);
            var variables = ast.FindAll(n => n is VariableExpressionAst, true).Cast<VariableExpressionAst>();
            Assert.DoesNotContain(variables, v => string.Equals(v.VariablePath.UserPath, "profile", System.StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Apply_script_on_a_missing_adapter_reports_it_and_changes_nothing()
        {
            var result = await PowerShellHost.RunAsync("Set-Ipv4Config.ps1", new Dictionary<string, object?>
            {
                ["InterfaceIndex"] = MissingInterface,
                ["UseDhcp"] = false,
                ["Addresses"] = new[] { "10.99.99.99/24" },
                ["Gateway"] = "",
                ["Dns"] = new string[0],
                ["SetPrivate"] = false,
            });
            Assert.False(result.Ok);
            Assert.Equal("NoIpv4Interface", result.Code);
        }

        [Fact]
        public async Task Category_script_on_a_missing_adapter_reports_it()
        {
            var result = await PowerShellHost.RunAsync("Set-NetworkCategory.ps1", new Dictionary<string, object?>
            {
                ["InterfaceIndex"] = MissingInterface,
                ["Category"] = "Private",
            });
            Assert.False(result.Ok);
            Assert.Equal("NoProfile", result.Code);
        }

        [Fact]
        public async Task Unknown_script_is_a_clean_failure()
        {
            var result = await PowerShellHost.RunAsync("Does-Not-Exist.ps1", new Dictionary<string, object?>());
            Assert.False(result.Ok);
            Assert.Equal("Failed", result.Code);
        }

        /// <summary>
        /// The fast WMI Hyper-V reader must see EXACTLY what the validated PowerShell script sees
        /// (VMs and states, adapters with MAC / switch / connected, switches with type / uplink /
        /// host access). Read-only. Skipped where Hyper-V is not installed.
        /// </summary>
        [Fact]
        public async Task Hyper_v_wmi_reader_matches_the_powershell_script()
        {
            if (Poni.Services.HyperV.HyperVService.Detect() != Poni.Services.HyperV.HyperVAvailability.Available) return;
            var script = await PowerShellHost.RunAsync("Get-HyperVState.ps1", new Dictionary<string, object?>());
            if (!script.Ok) return; // no read access on this machine: nothing to compare
            var expected = Poni.Core.HyperV.Rj45Rules.ParseState(script.Detail);
            var actual = Poni.Services.HyperV.WmiHyperVReader.Read();

            string Vms(Poni.Core.HyperV.HyperVState s) => string.Join(" | ", s.Vms.OrderBy(v => v.Name).Select(v => v.Name + "=" + v.State));
            string Adapters(Poni.Core.HyperV.HyperVState s) => string.Join(" | ", s.Adapters
                .OrderBy(a => a.VMName).ThenBy(a => a.Mac).ThenBy(a => a.Name)
                .Select(a => $"{a.VMName}/{a.Name}/{a.Mac}/{a.SwitchName}/{a.Connected}"));
            string Switches(Poni.Core.HyperV.HyperVState s) => string.Join(" | ", s.Switches.OrderBy(x => x.Name)
                .Select(x => $"{x.Name}/{x.Type}/{x.Uplink}/{x.HostAccess}"));

            Assert.Equal(Vms(expected), Vms(actual));
            Assert.Equal(Adapters(expected), Adapters(actual));
            Assert.Equal(Switches(expected), Switches(actual));
        }

        [Fact]
        public void Wmi_reader_reads_this_machine_without_error()
        {
            // Read-only. Every Windows machine (and CI runner) has at least one adapter.
            var adapters = WmiNetworkReader.ReadAdapters();
            Assert.NotEmpty(adapters);
            Assert.All(adapters, a => Assert.False(string.IsNullOrEmpty(a.Name)));
            Assert.True(adapters.Count(a => a.IsInternetAdapter) <= 1);
        }
    }
}
