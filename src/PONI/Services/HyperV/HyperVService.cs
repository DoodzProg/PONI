using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Security;
using System.Threading.Tasks;
using System.Management.Automation;
using Poni.Core.HyperV;
using Poni.Core.Network;
using Poni.Services.Network;

namespace Poni.Services.HyperV
{
    public enum HyperVAvailability
    {
        /// <summary>Hyper-V is installed and readable.</summary>
        Available,
        /// <summary>Hyper-V is not installed (e.g. Windows Home): the whole module is hidden.</summary>
        NotInstalled,
        /// <summary>Installed, but this account may not read it (not admin, not "Hyper-V Administrators").</summary>
        NoAccess,
        /// <summary>Installed, but its PowerShell module is missing.</summary>
        NoModule,
    }

    /// <summary>Hyper-V: detection, live state, RJ45 port routing, applying a profile inside a VM.</summary>
    public static class HyperVService
    {
        /// <summary>
        /// Cheap detection (a few ms, no PowerShell): does the Hyper-V WMI namespace exist?
        /// Readability is confirmed later by the first state read.
        /// </summary>
        public static HyperVAvailability Detect()
        {
#if DEBUG
            if (FakeStatePath != null) return HyperVAvailability.Available;
#endif
            try
            {
                new ManagementScope(@"\\.\root\virtualization\v2").Connect();
                return HyperVAvailability.Available;
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidNamespace)
            {
                return HyperVAvailability.NotInstalled;
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
            {
                return HyperVAvailability.NoAccess;
            }
            catch (UnauthorizedAccessException)
            {
                return HyperVAvailability.NoAccess;
            }
            catch (Exception ex)
            {
                Log.Warn("Hyper-V detection failed", ex);
                return HyperVAvailability.NotInstalled;
            }
        }

        /// <summary>Result of a state read: the state, or why it could not be read.</summary>
        public sealed class StateRead
        {
            public HyperVState? State { get; set; }
            public HyperVAvailability Availability { get; set; } = HyperVAvailability.Available;
            public string? Error { get; set; }
        }

        public static async Task<StateRead> ReadStateAsync()
        {
#if DEBUG
            if (FakeStatePath != null)
                return new StateRead { State = Rj45Rules.ParseState(File.ReadAllText(FakeStatePath)) };
#endif
            // Fast path: direct WMI (~0.4 s). The PowerShell script (7-10 s) stays as a fallback.
            try
            {
                return new StateRead { State = await Task.Run(WmiHyperVReader.Read).ConfigureAwait(false) };
            }
            catch (UnauthorizedAccessException ex)
            {
                return new StateRead { Availability = HyperVAvailability.NoAccess, Error = ex.Message };
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
            {
                return new StateRead { Availability = HyperVAvailability.NoAccess, Error = ex.Message };
            }
            catch (Exception ex)
            {
                Log.Warn("Hyper-V WMI read failed, falling back to PowerShell", ex);
            }

            var r = await PowerShellHost.RunAsync("Get-HyperVState.ps1", new Dictionary<string, object?>()).ConfigureAwait(false);
            if (r.Ok)
            {
                try { return new StateRead { State = Rj45Rules.ParseState(r.Detail) }; }
                catch (FormatException ex) { return new StateRead { Error = ex.Message }; }
            }
            Log.Warn("Hyper-V state read: " + r);
            return r.Code switch
            {
                "NoAccess" => new StateRead { Availability = HyperVAvailability.NoAccess, Error = r.Detail },
                "NoModule" => new StateRead { Availability = HyperVAvailability.NoModule, Error = r.Detail },
                _ => new StateRead { Error = r.Detail },
            };
        }

        public static async Task<NetworkOperationResult> ConnectRj45ToVmAsync(string vmName, string physicalAdapter)
        {
            if (IsDemo) return Demo();
            Log.Info($"RJ45 -> VM '{vmName}' (physical adapter '{physicalAdapter}')");
            var r = await PowerShellHost.RunAsync("Set-Rj45ToVm.ps1", new Dictionary<string, object?>
            {
                ["VMName"] = vmName,
                ["PhysicalAdapter"] = physicalAdapter,
            }).ConfigureAwait(false);
            Log.Info("RJ45 -> VM result: " + r);
            if (r.Ok) return Ok("Str.Rj45.ToVmDone", vmName);
            return r.Code switch
            {
                "VmMissing" => Fail("Str.Rj45.VmMissing", vmName),
                "PhysicalMissing" => Fail("Str.Rj45.PhysicalMissing", physicalAdapter),
                "PhysicalDisabled" => Fail("Str.Rj45.PhysicalDisabled", physicalAdapter),
                "SwitchOnOtherAdapter" => Fail("Str.Rj45.SwitchOnOtherAdapter", r.Detail),
                "AdapterUsedByOtherSwitch" => Fail("Str.Rj45.AdapterUsedByOtherSwitch", physicalAdapter, r.Detail),
                "NotConfirmed" => Fail("Str.Rj45.NotConfirmed", vmName),
                _ => Fail("Str.Rj45.Failed", r.Detail),
            };
        }

        public static async Task<NetworkOperationResult> ReturnRj45ToHostAsync(string? physicalAdapter)
        {
            if (IsDemo) return Demo();
            Log.Info($"RJ45 -> host (physical adapter '{physicalAdapter}')");
            var r = await PowerShellHost.RunAsync("Set-Rj45ToHost.ps1", new Dictionary<string, object?>
            {
                ["PhysicalAdapter"] = physicalAdapter ?? "",
            }).ConfigureAwait(false);
            Log.Info("RJ45 -> host result: " + r);
            if (r.Ok) return Ok("Str.Rj45.ToHostDone");
            return r.Code == "NotConfirmed" ? Fail("Str.Rj45.ToHostNotConfirmed") : Fail("Str.Rj45.Failed", r.Detail);
        }

        /// <summary>Unplugs VM adapters that point to a deleted switch (their VM cannot start otherwise).</summary>
        public static async Task<NetworkOperationResult> RepairDanglingAdaptersAsync()
        {
            if (IsDemo) return Demo();
            Log.Info("Repair VM adapters pointing to a deleted switch");
            var r = await PowerShellHost.RunAsync("Repair-VmAdapters.ps1", new Dictionary<string, object?>()).ConfigureAwait(false);
            Log.Info("Repair result: " + r);
            if (r.Ok) return Ok("Str.Rj45.RepairDone", r.Detail);
            return r.Code == "NotConfirmed" ? Fail("Str.Rj45.RepairIncomplete", r.Detail) : Fail("Str.Rj45.Failed", r.Detail);
        }

        /// <summary>Applies a configuration inside a running VM (PowerShell Direct, adapter found by MAC).</summary>
        public static async Task<NetworkOperationResult> ApplyInVmAsync(string vmName, VmAdapterInfo vmAdapter, string user, SecureString password,
                                                                      Ipv4Config config, bool rollback, bool allowPing)
        {
            if (IsDemo) return Demo();
            Log.Info($"Apply in VM '{vmName}' adapter '{vmAdapter.Name}' (MAC {vmAdapter.Mac}) as '{user}': " +
                     (config.UseDhcp ? "DHCP" : $"{string.Join(",", config.Addresses)} gw={config.Gateway ?? "-"} dns={string.Join(",", config.Dns)}"));
            var r = await PowerShellHost.RunAsync("Invoke-VmIpv4.ps1", new Dictionary<string, object?>
            {
                ["VMName"] = vmName,
                ["Credential"] = new PSCredential(user, password),
                ["Mac"] = vmAdapter.Mac,
                ["UseDhcp"] = config.UseDhcp,
                ["Addresses"] = config.Addresses.ToArray(),
                ["Gateway"] = config.Gateway ?? "",
                ["Dns"] = config.Dns.ToArray(),
                ["Rollback"] = rollback,
                ["AllowPing"] = allowPing,
                ["SetScript"] = PowerShellHost.LoadScript("Set-Ipv4Config.ps1"),
            }).ConfigureAwait(false);
            Log.Info("Apply in VM result: " + r);
            if (r.Ok)
            {
                // The IP is applied; the ping step (PONI's guest firewall rule) is reported apart.
                var ping = r.Detail.Contains("|ping=") ? r.Detail.Substring(r.Detail.IndexOf("|ping=", StringComparison.Ordinal) + 6) : "";
                if (ping == "allowed") return Ok("Str.Vm.AppliedPing", vmName);
                if (ping.StartsWith("failed:", StringComparison.Ordinal)) return Ok("Str.Vm.AppliedPingFailed", vmName, ping.Substring(7));
                return Ok("Str.Vm.Applied", vmName);
            }

            var result = r.Code switch
            {
                "VmMissing" => Fail("Str.Vm.Missing", vmName),
                "VmNotRunning" => Fail("Str.Vm.NotRunning", vmName),
                "Session" => Fail("Str.Vm.Session", vmName, r.Detail),
                "AdapterNotFound" => Fail("Str.Vm.AdapterNotFound", vmAdapter.Name, vmName),
                "Duplicate" => Fail("Str.Vm.Duplicate", vmName),
                _ => Fail("Str.Vm.Failed", vmName, Shorten(r.Detail)),
            };
            if (r.Detail.Contains("rollback=ok")) result.RolledBack = true;
            if (r.Detail.Contains("rollback=failed")) result.RollbackFailed = true;
            result.TechnicalDetail = r.ToString();
            return result;
        }

        private static bool _prewarmed;

        /// <summary>Loads the Hyper-V module in the shared PowerShell engine in the background (once).</summary>
        public static void Prewarm()
        {
            if (IsDemo || _prewarmed) return;
            _prewarmed = true;
            _ = PowerShellHost.RunTextAsync("Import-Module Hyper-V -ErrorAction SilentlyContinue");
        }

        private static string Shorten(string detail)
        {
            var main = detail.Split('|');
            return main.Length > 1 ? main[1] : detail;
        }

        private static NetworkOperationResult Ok(string key, params object[] args) =>
            new NetworkOperationResult { Success = true, MessageKey = key, Args = args };

        private static NetworkOperationResult Fail(string key, params object[] args) =>
            new NetworkOperationResult { Success = false, MessageKey = key, Args = args };

        // ------------------------------------------------------------ demo mode (Debug builds only)
        // PONI_FAKE_HYPERV=<json file>: fake Hyper-V state for UI work and screenshots on a machine
        // where the real VMs must not be touched. Compiled out of Release builds.
#if DEBUG
        private static string? FakeStatePath
        {
            get
            {
                var path = Environment.GetEnvironmentVariable("PONI_FAKE_HYPERV");
                return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
            }
        }

        public static bool IsDemo => FakeStatePath != null;
#else
        public static bool IsDemo => false;
#endif

        private static NetworkOperationResult Demo() => Fail("Str.HyperV.DemoMode");
    }
}
