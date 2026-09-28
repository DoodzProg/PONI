using System;
using System.Collections.Generic;

namespace Poni.Core
{
    /// <summary>Where a profile was last applied (purely informative: pre-fills the Apply dialog).</summary>
    public enum TargetKind { Host, VM }

    public sealed class LastTarget
    {
        public TargetKind Kind { get; set; }
        /// <summary>Host adapter name (Kind = Host).</summary>
        public string? Adapter { get; set; }
        /// <summary>VM name (Kind = VM).</summary>
        public string? VM { get; set; }
        /// <summary>Hyper-V side adapter name of the VM, e.g. "RJ45-Adapter" (Kind = VM).</summary>
        public string? VMAdapter { get; set; }
        public DateTime? AppliedOn { get; set; }

        public LastTarget Clone() => (LastTarget)MemberwiseClone();
    }

    /// <summary>A saved IPv4 configuration. The target (host or VM) is chosen at apply time, never stored here.</summary>
    public sealed class NetworkProfile
    {
        public string Name { get; set; } = "";
        public string IPAddress { get; set; } = "";
        public int PrefixLength { get; set; } = 24;
        /// <summary>Null or empty = no default gateway.</summary>
        public string? Gateway { get; set; }
        /// <summary>Empty = automatic DNS.</summary>
        public List<string> Dns { get; set; } = new List<string>();
        public DateTime? CreatedOn { get; set; }
        public LastTarget? LastTarget { get; set; }

        public string SubnetMask => Ipv4.PrefixToMaskString(PrefixLength);

        public NetworkProfile Clone()
        {
            var copy = (NetworkProfile)MemberwiseClone();
            copy.Dns = new List<string>(Dns);
            copy.LastTarget = LastTarget?.Clone();
            return copy;
        }
    }

    public enum ThemePreference { System, Light, Dark }

    /// <summary>Order of the profiles list. Custom = the stored order, rearranged by drag and drop.</summary>
    public enum ProfileSort { Custom, NameAsc, NameDesc, CreatedAsc, CreatedDesc }

    /// <summary>User settings persisted in the store.</summary>
    public sealed class AppSettings
    {
        /// <summary>"fr" or "en"; null = not chosen yet = English.</summary>
        public string? Language { get; set; }
        public ThemePreference Theme { get; set; } = ThemePreference.System;
        /// <summary>How the profiles list is sorted.</summary>
        public ProfileSort ProfileSort { get; set; } = ProfileSort.Custom;
        /// <summary>Hyper-V module (VMs / RJ45 port). Null = decide from detection at startup.</summary>
        public bool? HyperVEnabled { get; set; }
        /// <summary>Physical adapter used as the RJ45 port. Null = choose automatically.</summary>
        public string? Rj45PhysicalAdapter { get; set; }
        /// <summary>Force the network category to Private after applying (v1 did it silently).</summary>
        public bool SetPrivateOnApply { get; set; }
        /// <summary>Restore the previous configuration when applying fails.</summary>
        public bool RollbackOnFailure { get; set; } = true;
        /// <summary>
        /// Applying a profile inside a VM also lets it answer ping from the local network (PONI's own
        /// guest firewall rule 'PONI-Ping-In'); off = that rule is removed. On by default.
        /// </summary>
        public bool VmAllowPing { get; set; } = true;
        /// <summary>Sidebar folded to icons only.</summary>
        public bool SidebarCollapsed { get; set; }
        /// <summary>Profiles list: detailed columns instead of the compact view.</summary>
        public bool DetailedView { get; set; }
        /// <summary>
        /// Last user name that WORKED for each VM (PowerShell Direct), never a password. v1 kept
        /// every typed name, failed attempts included, which is why it was removed there.
        /// </summary>
        public Dictionary<string, string> VmUsers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Everything PONI persists (store.json).</summary>
    public sealed class StoreData
    {
        public const int CurrentSchemaVersion = 2;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public AppSettings Settings { get; set; } = new AppSettings();
        public List<NetworkProfile> Profiles { get; set; } = new List<NetworkProfile>();
    }
}
