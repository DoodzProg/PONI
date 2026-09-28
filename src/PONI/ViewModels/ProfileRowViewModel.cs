using System.Linq;
using Poni.Core;
using Poni.Core.Network;

namespace Poni.ViewModels
{
    /// <summary>Where a dragged profile would land relative to this line (blue line above / below).</summary>
    public enum DropHint { None, Above, Below }

    /// <summary>One line of the profiles list.</summary>
    public sealed class ProfileRowViewModel : Infrastructure.ObservableObject
    {
        private DropHint _dropHint;

        public DropHint DropHint { get => _dropHint; set => SetProperty(ref _dropHint, value); }

        public ProfileRowViewModel(ProfilesViewModel owner, NetworkProfile profile, int index, AdapterInfo? activeOn, bool isFirst)
        {
            Owner = owner;
            Profile = profile;
            AvatarIndex = index % 4; // the four cable colours of the PONI logo
            ActiveOn = activeOn;
            IsFirst = isFirst;
        }

        /// <summary>The page (its commands are reached from menus, which live outside the visual tree).</summary>
        public ProfilesViewModel Owner { get; }
        public NetworkProfile Profile { get; }
        public int AvatarIndex { get; }
        public bool IsFirst { get; }
        /// <summary>The adapter currently running exactly this profile (badge "Active").</summary>
        public AdapterInfo? ActiveOn { get; }
        public bool IsActive => ActiveOn != null;

        public string Name => Profile.Name;
        public string Initial => ProfileText.Initial(Profile.Name);
        public string Meta => ProfileText.Meta(Profile);
        public string IpText => Profile.IPAddress;
        public string MaskText => Profile.SubnetMask;
        public string GatewayText => string.IsNullOrEmpty(Profile.Gateway) ? "—" : Profile.Gateway!;
        public string DnsText => Profile.Dns.Count == 0 ? "—" : string.Join(", ", Profile.Dns);
        public string TargetText => ProfileText.Target(Profile.LastTarget);
        public string WhenText => ProfileText.When(Profile.LastTarget?.AppliedOn);
        public bool HasWhen => Profile.LastTarget?.AppliedOn != null;
        /// <summary>
        /// "Applied · Ethernet": the profile is the configuration of that adapter right now.
        /// Green when the adapter is connected (really in use), grey when it is unplugged
        /// (in place but not in use) - "Active" alone was misleading (user feedback, 2026-09-28).
        /// </summary>
        public bool IsActiveConnected => ActiveOn?.IsConnected == true;
        public string ActiveText => ActiveOn == null ? "" : Services.LocalizationService.Format("Str.Profile.AppliedOn", ActiveOn.Name);
        public string ActiveTooltip => ActiveOn == null ? "" : Services.LocalizationService.Format(
            IsActiveConnected ? "Str.Profile.AppliedTooltip" : "Str.Profile.AppliedUnpluggedTooltip", ActiveOn.Name);

        public bool Matches(string filter) =>
            string.IsNullOrWhiteSpace(filter)
            || Profile.Name.IndexOf(filter.Trim(), System.StringComparison.CurrentCultureIgnoreCase) >= 0
            || Profile.IPAddress.Contains(filter.Trim())
            || (Profile.Gateway ?? "").Contains(filter.Trim())
            || Profile.Dns.Any(d => d.Contains(filter.Trim()));
    }
}
