using System;
using System.Collections.Generic;
using System.Linq;

namespace Poni.Core.HyperV
{
    public sealed class VmInfo
    {
        public string Name { get; set; } = "";
        /// <summary>Hyper-V state as text: Running, Off, Saved, Paused, Starting...</summary>
        public string State { get; set; } = "";
        public bool IsRunning => string.Equals(State, "Running", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A virtual network adapter of a VM (Hyper-V side).</summary>
    public sealed class VmAdapterInfo
    {
        public string VMName { get; set; } = "";
        /// <summary>Hyper-V side name, e.g. "RJ45-Adapter" (not the "Ethernet N" name inside the guest).</summary>
        public string Name { get; set; } = "";
        /// <summary>Empty when not connected to any switch.</summary>
        public string SwitchName { get; set; } = "";
        /// <summary>12 hex digits, no separators; "000000000000" = dynamic MAC not assigned yet (VM off).</summary>
        public string Mac { get; set; } = "";
        public bool HasUsableMac => Mac.Length == 12 && Mac != "000000000000";
        public bool Connected { get; set; }

        /// <summary>
        /// Still "connected" to a switch that no longer exists: Hyper-V refuses to start the VM
        /// (misleading "insufficient system resources" error, real test of 2026-09-28).
        /// </summary>
        public bool IsDangling => Connected && SwitchName.Length == 0;
    }

    public sealed class SwitchInfo
    {
        public string Name { get; set; } = "";
        /// <summary>External, Internal or Private.</summary>
        public string Type { get; set; } = "";
        /// <summary>Interface description of the physical adapter behind an External switch.</summary>
        public string Uplink { get; set; } = "";
        /// <summary>AllowManagementOS: the host keeps an IP stack on the uplink.</summary>
        public bool HostAccess { get; set; }
    }

    /// <summary>A snapshot of Hyper-V: VMs, their adapters, the virtual switches.</summary>
    public sealed class HyperVState
    {
        public List<VmInfo> Vms { get; set; } = new List<VmInfo>();
        public List<VmAdapterInfo> Adapters { get; set; } = new List<VmAdapterInfo>();
        public List<SwitchInfo> Switches { get; set; } = new List<SwitchInfo>();
        public DateTime ReadAt { get; set; } = DateTime.Now;

        public IEnumerable<VmAdapterInfo> AdaptersOf(string vmName) =>
            Adapters.Where(a => string.Equals(a.VMName, vmName, StringComparison.OrdinalIgnoreCase));

        /// <summary>VM adapters pointing to a deleted switch (their VM cannot start).</summary>
        public List<VmAdapterInfo> DanglingAdapters => Adapters.Where(a => a.IsDangling).ToList();

        public VmInfo? FindVm(string name) =>
            Vms.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public enum Rj45Mode
    {
        /// <summary>No RJ45 switch: the physical port belongs to Windows.</summary>
        Host,
        /// <summary>The RJ45 switch exists and exactly one VM is connected to it.</summary>
        Vm,
        /// <summary>Several VMs share the port (v1 could leave the previous VM attached).</summary>
        Shared,
        /// <summary>The RJ45 switch exists but no VM uses it: Windows has lost the port for nothing.</summary>
        Orphan,
    }

    /// <summary>Where the RJ45 port points, computed from the LIVE Hyper-V state.</summary>
    public sealed class Rj45Status
    {
        public Rj45Mode Mode { get; set; }
        /// <summary>VMs connected to the RJ45 switch (1 in Vm mode, 2+ in Shared mode).</summary>
        public List<string> ConnectedVms { get; set; } = new List<string>();
        /// <summary>The RJ45 switch, when it exists.</summary>
        public SwitchInfo? Switch { get; set; }

        public string? CurrentVm => Mode == Rj45Mode.Vm ? ConnectedVms.FirstOrDefault() : null;
        public bool SwitchExists => Switch != null;
    }
}
