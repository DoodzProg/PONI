using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using Poni.Core.HyperV;

namespace Poni.Services.HyperV
{
    /// <summary>
    /// Reads Hyper-V directly through WMI (root\virtualization\v2): ~0.4 s, against 7-10 s for the
    /// Hyper-V PowerShell cmdlets (measured on real hardware, 2026-09-28). Read-only.
    /// Mapping validated against Get-VM / Get-VMNetworkAdapter / Get-VMSwitch on the real PC:
    ///   Msvm_ComputerSystem (all but the host)            -> VMs (EnabledState: 2 running, 3 off...)
    ///   Msvm_Synthetic/EmulatedEthernetPortSettingData    -> VM adapters (name, MAC), per VM GUID
    ///   Msvm_EthernetPortAllocationSettingData            -> connection of an adapter to a switch
    ///       (EnabledState 2 = connected, HostResource = the switch; missing switch = dangling)
    ///   Msvm_VirtualEthernetSwitch + its allocations       -> switches, type, uplink, host access
    /// Checkpoints have their own settings objects (their GUID is not a VM's): they are ignored.
    /// </summary>
    public static class WmiHyperVReader
    {
        private const string Namespace = @"\\.\root\virtualization\v2";
        // The key property Name, NOT the "Name=" inside CreationClassName="..." that comes first in the
        // path (bug caught by the WMI-vs-PowerShell comparison test).
        private static readonly Regex NameInPath = new Regex("[,.]Name=\"([^\"]+)\"", RegexOptions.Compiled);

        public static HyperVState Read()
        {
            var scope = new ManagementScope(Namespace);
            scope.Connect();
            var state = new HyperVState();

            // ---- VMs (every computer system except the host itself; Caption is localized, not usable)
            var vmsById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mo in Query(scope, "SELECT ElementName, Name, EnabledState FROM Msvm_ComputerSystem"))
            {
                var id = mo["Name"] as string ?? "";
                if (string.Equals(id, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) continue;
                var name = mo["ElementName"] as string ?? id;
                vmsById[id] = name;
                state.Vms.Add(new VmInfo { Name = name, State = StateName(ToInt(mo["EnabledState"])) });
            }

            // ---- switches
            var switchesById = new Dictionary<string, SwitchInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var mo in Query(scope, "SELECT ElementName, Name FROM Msvm_VirtualEthernetSwitch"))
            {
                var info = new SwitchInfo { Name = mo["ElementName"] as string ?? "", Type = "Private" };
                switchesById[mo["Name"] as string ?? ""] = info;
                state.Switches.Add(info);
            }
            var externalPorts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mo in Query(scope, "SELECT Name, ElementName FROM Msvm_ExternalEthernetPort"))
                externalPorts[mo["Name"] as string ?? ""] = mo["ElementName"] as string ?? "";

            // ---- allocations: switch ports (type / uplink / host access) and VM adapter connections
            var allocations = new List<(string InstanceId, string HostResource, int Enabled)>();
            foreach (var mo in Query(scope, "SELECT InstanceID, HostResource, EnabledState FROM Msvm_EthernetPortAllocationSettingData"))
            {
                var instance = mo["InstanceID"] as string ?? "";
                if (instance.StartsWith("Microsoft:Definition", StringComparison.OrdinalIgnoreCase)) continue;
                var host = mo["HostResource"] is string[] hr ? string.Join("", hr) : "";
                allocations.Add((instance, host, ToInt(mo["EnabledState"])));

                // A switch's own ports: owner GUID = the switch.
                var owner = OwnerGuid(instance);
                if (!switchesById.TryGetValue(owner, out var sw)) continue;
                if (host.Contains(":Msvm_ExternalEthernetPort."))
                {
                    sw.Type = "External";
                    var portName = NameInPath.Match(host).Groups[1].Value;
                    sw.Uplink = externalPorts.TryGetValue(portName, out var description) ? description : "";
                }
                else if (host.Contains(":Msvm_ComputerSystem."))
                {
                    sw.HostAccess = true;
                    if (sw.Type == "Private") sw.Type = "Internal";
                }
            }

            // ---- VM adapters (synthetic, and legacy emulated ones)
            foreach (var cls in new[] { "Msvm_SyntheticEthernetPortSettingData", "Msvm_EmulatedEthernetPortSettingData" })
            {
                foreach (var mo in Query(scope, "SELECT ElementName, Address, InstanceID FROM " + cls))
                {
                    var instance = mo["InstanceID"] as string ?? "";
                    if (!vmsById.TryGetValue(OwnerGuid(instance), out var vmName)) continue; // definitions, checkpoints
                    var adapter = new VmAdapterInfo
                    {
                        VMName = vmName,
                        Name = mo["ElementName"] as string ?? "",
                        Mac = Rj45Rules.NormalizeMac(mo["Address"] as string),
                    };
                    var connection = allocations.FirstOrDefault(a => a.InstanceId.StartsWith(instance + "\\", StringComparison.OrdinalIgnoreCase));
                    if (connection.InstanceId != null)
                    {
                        adapter.Connected = connection.Enabled == 2;
                        var switchId = connection.HostResource.Contains(":Msvm_VirtualEthernetSwitch.")
                            ? NameInPath.Match(connection.HostResource).Groups[1].Value
                            : "";
                        // Connected to a switch that exists: its name; to a deleted one: empty (dangling).
                        adapter.SwitchName = adapter.Connected && switchesById.TryGetValue(switchId, out var target) ? target.Name : "";
                    }
                    state.Adapters.Add(adapter);
                }
            }

            state.Vms = state.Vms.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            return state;
        }

        /// <summary>"Microsoft:1B0CB0F6-ADE6-...\A0210396-..." -> "1B0CB0F6-ADE6-..." (VM or switch GUID).</summary>
        private static string OwnerGuid(string instanceId)
        {
            const string prefix = "Microsoft:";
            if (!instanceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return "";
            var rest = instanceId.Substring(prefix.Length);
            var slash = rest.IndexOf('\\');
            return slash < 0 ? rest : rest.Substring(0, slash);
        }

        /// <summary>Msvm_ComputerSystem.EnabledState -> the names Get-VM uses.</summary>
        private static string StateName(int enabledState) => enabledState switch
        {
            2 => "Running",
            3 => "Off",
            32768 => "Paused",
            32769 => "Saved",
            10 or 32770 => "Starting",
            4 or 32774 => "Stopping",
            32773 => "Saving",
            32776 => "Pausing",
            32777 => "Resuming",
            _ => "Other",
        };

        private static IEnumerable<ManagementBaseObject> Query(ManagementScope scope, string wql)
        {
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
            using var results = searcher.Get();
            foreach (ManagementBaseObject mo in results) yield return mo;
        }

        private static int ToInt(object? value)
        {
            try { return value == null ? 0 : Convert.ToInt32(value); }
            catch { return 0; }
        }
    }
}
