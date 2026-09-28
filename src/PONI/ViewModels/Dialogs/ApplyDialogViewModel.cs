using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Poni.Core;
using Poni.Core.HyperV;
using Poni.Core.Network;
using Poni.Infrastructure;
using Poni.Services;
using Poni.Services.HyperV;

namespace Poni.ViewModels
{
    /// <summary>Where the user chose to apply a profile.</summary>
    public sealed class ApplyTarget
    {
        public AdapterInfo? HostAdapter { get; set; }
        /// <summary>The host adapter is the RJ45 port, currently given to a VM: release it first (v1 bug #2).</summary>
        public bool ReleaseRj45First { get; set; }
        public string? VmName { get; set; }
        public VmAdapterInfo? VmAdapter { get; set; }
    }

    /// <summary>One host adapter the profile can be applied to.</summary>
    public sealed class AdapterChoice : ObservableObject
    {
        private bool _isSelected;

        /// <param name="rj45HeldBy">When this adapter is the RJ45 port given to VMs: their names (empty = orphan switch).</param>
        public AdapterChoice(AdapterInfo adapter, IList<string>? rj45HeldBy = null)
        {
            Adapter = adapter;
            Rj45HeldBy = rj45HeldBy;
            Blocker = NetworkRules.CanApply(adapter);
            IsRisky = NetworkRules.IsRisky(adapter);
        }

        public AdapterInfo Adapter { get; }
        public IList<string>? Rj45HeldBy { get; }
        public bool IsRj45Released => Rj45HeldBy != null;
        public ApplyBlocker Blocker { get; }
        /// <summary>Usable as is, or after giving the RJ45 port back to Windows.</summary>
        public bool IsAvailable => Blocker == ApplyBlocker.None || (Blocker == ApplyBlocker.NoIpv4 && IsRj45Released);
        public bool IsRisky { get; }
        public string Name => Adapter.Name;

        public string Detail
        {
            get
            {
                if (IsRj45Released)
                {
                    return Rj45HeldBy!.Count == 0
                        ? LocalizationService.Get("Str.Apply.Rj45Orphan")
                        : LocalizationService.Format("Str.Apply.Rj45HeldBy", string.Join(", ", Rj45HeldBy));
                }
                if (Blocker == ApplyBlocker.Disabled) return LocalizationService.Get("Str.Adapter.Disabled");
                if (Blocker == ApplyBlocker.NoIpv4) return LocalizationService.Get("Str.Adapter.NoIpv4");
                var state = LocalizationService.Get(Adapter.IsConnected ? "Str.Adapter.Connected" : "Str.Adapter.Unplugged");
                var address = Adapter.PrimaryAddress;
                return address == null ? state : state + " · " + address.Address;
            }
        }

        public string? Badge => IsRj45Released
            ? LocalizationService.Get("Str.Apply.Rj45Badge")
            : IsRisky && IsAvailable
                ? LocalizationService.Get(Adapter.IsInternetAdapter ? "Str.Apply.InternetBadge" : "Str.Apply.WifiBadge")
                : null;

        public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    }

    public sealed class VmChoice : ObservableObject
    {
        private bool _isSelected;
        private readonly System.Action<VmChoice> _onSelected;

        public VmChoice(VmInfo vm, System.Action<VmChoice> onSelected)
        {
            Vm = vm;
            _onSelected = onSelected;
        }

        public VmInfo Vm { get; }
        public string Name => Vm.Name;

        public bool IsSelected
        {
            get => _isSelected;
            set { if (SetProperty(ref _isSelected, value) && value) _onSelected(this); }
        }
    }

    public sealed class VmAdapterChoice : ObservableObject
    {
        private bool _isSelected;

        public VmAdapterChoice(VmAdapterInfo adapter) => Adapter = adapter;

        public VmAdapterInfo Adapter { get; }
        public string Name => Adapter.Name;
        public string Detail => string.IsNullOrEmpty(Adapter.SwitchName)
            ? LocalizationService.Get("Str.Apply.VmAdapterNoSwitch")
            : LocalizationService.Format("Str.Apply.VmAdapterOnSwitch", Adapter.SwitchName);
        public bool IsAvailable => Adapter.HasUsableMac;
        public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    }

    /// <summary>
    /// "Apply profile X": this computer (a host adapter) or a virtual machine (a RUNNING VM and one
    /// of its adapters; PONI never starts / stops VMs). Result: an ApplyTarget.
    /// </summary>
    public sealed class ApplyDialogViewModel : DialogViewModel
    {
        private bool _isVmTab;

        public ApplyDialogViewModel(NetworkProfile profile, IEnumerable<AdapterInfo> adapters, bool rollbackEnabled, HyperVContext hyperV)
        {
            Profile = profile;
            RollbackEnabled = rollbackEnabled;
            var hostAdapters = adapters.ToList();

            // RJ45 port given to a VM: its uplink adapter can still be picked (released first).
            AdapterInfo? rj45Uplink = null;
            List<string>? heldBy = null;
            var status = hyperV.IsEnabled ? hyperV.Rj45 : null;
            if (status != null && status.SwitchExists)
            {
                rj45Uplink = Rj45Rules.UplinkOf(status, hostAdapters);
                heldBy = status.ConnectedVms;
            }
            Choices = hostAdapters
                .Where(a => NetworkRules.IsWorthListing(a) || a == rj45Uplink)
                .Select(a => new AdapterChoice(a, a == rj45Uplink ? heldBy : null))
                .ToList();

            var last = profile.LastTarget;
            var lastHost = last?.Kind == TargetKind.Host ? last.Adapter : null;
            var pick = Choices.FirstOrDefault(c => c.IsAvailable && c.Name == lastHost)
                       ?? Choices.FirstOrDefault(c => c.IsAvailable && !c.IsRisky && !c.Adapter.IsVirtual)
                       ?? Choices.FirstOrDefault(c => c.IsAvailable);
            if (pick != null) pick.IsSelected = true;
            foreach (var c in Choices) c.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(AdapterChoice.IsSelected)) return;
                OnPropertyChanged(nameof(ShowRiskWarning));
                OnPropertyChanged(nameof(ShowRj45Release));
            };

            // Virtual machines (Hyper-V module on): running ones only.
            VmTabAvailable = hyperV.IsEnabled && hyperV.State != null;
            if (VmTabAvailable)
            {
                State = hyperV.State!;
                foreach (var vm in State.Vms.Where(v => v.IsRunning)) Vms.Add(new VmChoice(vm, OnVmSelected));
                AllVmsStopped = State.Vms.Count > 0 && Vms.Count == 0;
                var lastVm = last?.Kind == TargetKind.VM ? Vms.FirstOrDefault(v => v.Name == last.VM) : null;
                (lastVm ?? Vms.FirstOrDefault())?.SetSelected();
                if (lastVm != null) IsVmTab = true;
            }

            ConfirmCommand = new RelayCommand(Confirm, () => IsVmTab ? SelectedVmAdapter != null : SelectedHost != null);
        }

        public NetworkProfile Profile { get; }
        public bool RollbackEnabled { get; }
        private HyperVState? State { get; }

        public IReadOnlyList<AdapterChoice> Choices { get; }
        public ObservableCollection<VmChoice> Vms { get; } = new ObservableCollection<VmChoice>();
        public ObservableCollection<VmAdapterChoice> VmAdapters { get; } = new ObservableCollection<VmAdapterChoice>();

        public bool VmTabAvailable { get; }
        public bool AllVmsStopped { get; }
        public bool NoVmAtAll => VmTabAvailable && State!.Vms.Count == 0;
        public bool HasRunningVms => Vms.Count > 0;

        public bool IsVmTab
        {
            get => _isVmTab;
            set
            {
                if (!SetProperty(ref _isVmTab, value)) return;
                OnPropertyChanged(nameof(IsHostTab));
                OnPropertyChanged(nameof(ShowRiskWarning));
                OnPropertyChanged(nameof(ShowRj45Release));
                OnPropertyChanged(nameof(ShowVmNoSwitchWarning));
            }
        }

        public bool IsHostTab
        {
            get => !_isVmTab;
            set { if (value) IsVmTab = false; }
        }

        public AdapterChoice? SelectedHost => Choices.FirstOrDefault(c => c.IsSelected && c.IsAvailable);
        public VmAdapterChoice? SelectedVmAdapter => VmAdapters.FirstOrDefault(a => a.IsSelected && a.IsAvailable);
        public VmChoice? SelectedVm => Vms.FirstOrDefault(v => v.IsSelected);

        public string Title => LocalizationService.Format("Str.Apply.Title", Profile.Name);
        public string Summary => ProfileText.Meta(Profile);
        public bool ShowRiskWarning => !IsVmTab && SelectedHost?.IsRisky == true && SelectedHost?.IsRj45Released == false;
        public bool ShowRj45Release => !IsVmTab && SelectedHost?.IsRj45Released == true;
        /// <summary>What PONI will do with its ping rule inside the VM (Settings › Hyper-V).</summary>
        public string VmPingNote => LocalizationService.Get(AppData.Data.Settings.VmAllowPing ? "Str.Apply.VmPingOn" : "Str.Apply.VmPingOff");
        public string VmTabTooltip => LocalizationService.Get(VmTabAvailable ? "Str.Apply.VmTabHint" : "Str.Apply.VmOff");

        public RelayCommand ConfirmCommand { get; }

        public override double DialogWidth => 520;

        private void OnVmSelected(VmChoice chosen)
        {
            foreach (var v in Vms) if (v != chosen) v.IsSelected = false;
            VmAdapters.Clear();
            var adapters = State!.AdaptersOf(chosen.Name).ToList();
            foreach (var a in adapters) VmAdapters.Add(new VmAdapterChoice(a));
            var last = Profile.LastTarget?.Kind == TargetKind.VM && Profile.LastTarget.VM == chosen.Name ? Profile.LastTarget.VMAdapter : null;
            var preferred = Rj45Rules.PreferredVmAdapter(adapters.Where(a => a.HasUsableMac), last);
            var choice = VmAdapters.FirstOrDefault(c => c.Adapter == preferred);
            if (choice != null) choice.IsSelected = true;
            foreach (var c in VmAdapters) c.PropertyChanged += (_, __) => OnPropertyChanged(nameof(ShowVmNoSwitchWarning));
            OnPropertyChanged(nameof(ShowVmNoSwitchWarning));
        }

        /// <summary>
        /// The chosen VM adapter is not plugged into any switch: the IP is stored, but the guest sees
        /// "media disconnected" and ipconfig shows no address until it is plugged (real test 2026-09-28).
        /// </summary>
        public bool ShowVmNoSwitchWarning => IsVmTab && SelectedVmAdapter != null && string.IsNullOrEmpty(SelectedVmAdapter.Adapter.SwitchName);

        private void Confirm()
        {
            if (IsVmTab)
            {
                var vm = SelectedVm;
                var adapter = SelectedVmAdapter;
                if (vm != null && adapter != null) Close(new ApplyTarget { VmName = vm.Name, VmAdapter = adapter.Adapter });
                return;
            }
            var host = SelectedHost;
            if (host != null) Close(new ApplyTarget { HostAdapter = host.Adapter, ReleaseRj45First = host.IsRj45Released });
        }
    }

    internal static class VmChoiceExtensions
    {
        public static void SetSelected(this VmChoice choice) => choice.IsSelected = true;
    }
}
