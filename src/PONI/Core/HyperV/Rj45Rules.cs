using System;
using System.Collections.Generic;
using System.Linq;
using Poni.Core.Network;

namespace Poni.Core.HyperV
{
    public static class Rj45Rules
    {
        /// <summary>The ONLY switch PONI ever creates or removes (never a user's own switch).</summary>
        public const string SwitchName = "RJ45-Switch";

        /// <summary>Name of the VM adapter PONI manages; v1 called it "Carte-RJ45" (still recognised).</summary>
        public const string AdapterName = "RJ45-Adapter";
        public const string LegacyAdapterName = "Carte-RJ45";

        public static bool IsPoniAdapter(VmAdapterInfo a) =>
            string.Equals(a.Name, AdapterName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(a.Name, LegacyAdapterName, StringComparison.OrdinalIgnoreCase);

        /// <summary>Computes where the RJ45 port points from the live Hyper-V state (never from memory).</summary>
        public static Rj45Status Compute(HyperVState state)
        {
            var status = new Rj45Status
            {
                Switch = state.Switches.FirstOrDefault(s => string.Equals(s.Name, SwitchName, StringComparison.OrdinalIgnoreCase)),
            };
            if (status.Switch == null)
            {
                status.Mode = Rj45Mode.Host;
                return status;
            }

            status.ConnectedVms = state.Adapters
                .Where(a => string.Equals(a.SwitchName, SwitchName, StringComparison.OrdinalIgnoreCase) && a.VMName.Length > 0)
                .Select(a => a.VMName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            status.Mode = status.ConnectedVms.Count switch
            {
                0 => Rj45Mode.Orphan,
                1 => Rj45Mode.Vm,
                _ => Rj45Mode.Shared,
            };
            return status;
        }

        /// <summary>
        /// The physical adapter used as the RJ45 port (v1 hard-coded "Ethernet").
        /// The user's choice if it still exists; otherwise the wired physical adapter named
        /// "Ethernet", else the first wired physical one. Wi-Fi is never a candidate: an external
        /// Hyper-V switch on Wi-Fi does not behave like a real port.
        /// </summary>
        public static AdapterInfo? PickPhysicalAdapter(IEnumerable<AdapterInfo> adapters, string? preferred)
        {
            var wired = adapters.Where(IsCandidate).ToList();
            if (!string.IsNullOrEmpty(preferred))
            {
                var chosen = wired.FirstOrDefault(a => string.Equals(a.Name, preferred, StringComparison.OrdinalIgnoreCase));
                if (chosen != null) return chosen;
            }
            return wired.FirstOrDefault(a => string.Equals(a.Name, "Ethernet", StringComparison.OrdinalIgnoreCase))
                   ?? wired.FirstOrDefault();
        }

        public static bool IsCandidate(AdapterInfo a) => a.Kind == AdapterKind.Ethernet;

        /// <summary>
        /// The host adapter serving as uplink of the RJ45 switch (it has no IPv4 stack while the
        /// switch exists): matched on the interface description reported by Hyper-V.
        /// </summary>
        public static AdapterInfo? UplinkOf(Rj45Status status, IEnumerable<AdapterInfo> adapters)
        {
            var uplink = status.Switch?.Uplink;
            if (string.IsNullOrEmpty(uplink)) return null;
            return adapters.FirstOrDefault(a => string.Equals(a.Description, uplink, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The VM adapter to pre-select when applying a profile inside a VM.</summary>
        public static VmAdapterInfo? PreferredVmAdapter(IEnumerable<VmAdapterInfo> vmAdapters, string? lastUsed)
        {
            var list = vmAdapters.ToList();
            return list.FirstOrDefault(a => !string.IsNullOrEmpty(lastUsed) && string.Equals(a.Name, lastUsed, StringComparison.OrdinalIgnoreCase))
                   ?? list.FirstOrDefault(a => string.Equals(a.SwitchName, SwitchName, StringComparison.OrdinalIgnoreCase))
                   ?? list.FirstOrDefault(IsPoniAdapter)
                   ?? list.FirstOrDefault();
        }

        /// <summary>Reads the JSON produced by Scripts/Get-HyperVState.ps1 (PowerShell ConvertTo-Json).</summary>
        public static HyperVState ParseState(string json)
        {
            var root = Json.AsObject(Json.Parse(json)) ?? throw new FormatException("Hyper-V state is not a JSON object.");
            var state = new HyperVState();
            foreach (var item in Json.AsList(Json.Get(root, "Vms")))
            {
                var o = Json.AsObject(item);
                if (o == null) continue;
                state.Vms.Add(new VmInfo { Name = Json.AsString(Json.Get(o, "Name")) ?? "", State = Json.AsString(Json.Get(o, "State")) ?? "" });
            }
            foreach (var item in Json.AsList(Json.Get(root, "Adapters")))
            {
                var o = Json.AsObject(item);
                if (o == null) continue;
                state.Adapters.Add(new VmAdapterInfo
                {
                    VMName = Json.AsString(Json.Get(o, "VMName")) ?? "",
                    Name = Json.AsString(Json.Get(o, "Name")) ?? "",
                    SwitchName = Json.AsString(Json.Get(o, "SwitchName")) ?? "",
                    Mac = NormalizeMac(Json.AsString(Json.Get(o, "Mac"))),
                    Connected = Json.AsBool(Json.Get(o, "Connected")) ?? false,
                });
            }
            foreach (var item in Json.AsList(Json.Get(root, "Switches")))
            {
                var o = Json.AsObject(item);
                if (o == null) continue;
                state.Switches.Add(new SwitchInfo
                {
                    Name = Json.AsString(Json.Get(o, "Name")) ?? "",
                    Type = Json.AsString(Json.Get(o, "Type")) ?? "",
                    Uplink = Json.AsString(Json.Get(o, "Uplink")) ?? "",
                    HostAccess = Json.AsBool(Json.Get(o, "HostAccess")) ?? false,
                });
            }
            state.Vms = state.Vms.Where(v => v.Name.Length > 0).OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            return state;
        }

        /// <summary>"00-15-5D-38-01-0A" / "00:15:5d:38:01:0a" / "00155D38010A" -> "00155D38010A".</summary>
        public static string NormalizeMac(string? mac) =>
            (mac ?? "").Replace("-", "").Replace(":", "").Trim().ToUpperInvariant();
    }
}
