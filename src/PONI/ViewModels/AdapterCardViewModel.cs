using System.Linq;
using Poni.Core.Network;
using Poni.Services;

namespace Poni.ViewModels
{
    public enum AdapterState { Connected, Unplugged, Disabled, NoIpv4 }

    /// <summary>One card of "This machine": an adapter and its live configuration.</summary>
    public sealed class AdapterCardViewModel
    {
        public AdapterCardViewModel(ProfilesViewModel owner, AdapterInfo adapter)
        {
            Owner = owner;
            Adapter = adapter;
            State = !adapter.IsEnabled ? AdapterState.Disabled
                  : !adapter.HasIpv4 ? AdapterState.NoIpv4
                  : adapter.IsConnected ? AdapterState.Connected
                  : AdapterState.Unplugged;
        }

        /// <summary>The page (its commands are reached from the card menu).</summary>
        public ProfilesViewModel Owner { get; }
        public AdapterInfo Adapter { get; }
        public AdapterState State { get; }
        public string Name => Adapter.Name;
        public string Description => Adapter.Description;
        public bool IsWiFi => Adapter.Kind == AdapterKind.WiFi;
        public bool IsVirtual => Adapter.IsVirtual;
        public bool IsDimmed => State != AdapterState.Connected;
        public bool IsInternet => Adapter.IsInternetAdapter;
        public bool HasDuplicate => Adapter.Addresses.Any(a => a.IsDuplicate);

        public string StatusText => LocalizationService.Get(State switch
        {
            AdapterState.Connected => "Str.Adapter.Connected",
            AdapterState.Disabled => "Str.Adapter.Disabled",
            AdapterState.NoIpv4 => "Str.Adapter.NoIpv4",
            _ => "Str.Adapter.Unplugged",
        });

        public string ModeText => !Adapter.HasIpv4 ? "—" : LocalizationService.Get(Adapter.Dhcp ? "Str.Adapter.Dhcp" : "Str.Adapter.Manual");

        public string IpText
        {
            get
            {
                var a = Adapter.PrimaryAddress;
                return a == null ? "—" : a.Address + " / " + a.PrefixLength;
            }
        }

        public string GatewayText => Adapter.PrimaryGateway ?? "—";
        public string DnsText => Adapter.DnsServers.Count == 0 ? "—" : string.Join(", ", Adapter.DnsServers);

        public string CategoryText => LocalizationService.Get(Adapter.Category switch
        {
            NetworkCategory.Private => "Str.Adapter.Private",
            NetworkCategory.Public => "Str.Adapter.Public",
            NetworkCategory.Domain => "Str.Adapter.Domain",
            _ => "Str.Adapter.NoNetwork",
        });

        // Actions offered in the card menu.
        public bool CanConfigure => NetworkRules.CanApply(Adapter) == ApplyBlocker.None;
        public bool CanSetDhcp => CanConfigure && !Adapter.Dhcp;
        public bool CanMakePrivate => Adapter.Category == NetworkCategory.Public;
        public bool CanMakePublic => Adapter.Category == NetworkCategory.Private;
    }
}
