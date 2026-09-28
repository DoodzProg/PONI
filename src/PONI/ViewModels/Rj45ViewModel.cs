using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Poni.Core.HyperV;
using Poni.Core.Network;
using Poni.Infrastructure;
using Poni.Services;
using Poni.Services.HyperV;
using Poni.Services.Network;

namespace Poni.ViewModels
{
    /// <summary>A card of the "Destinations" grid: Windows (host) or a VM.</summary>
    public sealed class Rj45DestinationViewModel
    {
        public Rj45DestinationViewModel(Rj45ViewModel owner, VmInfo? vm, bool isCurrent)
        {
            Owner = owner;
            Vm = vm;
            IsCurrent = isCurrent;
            // Own command, no CommandParameter: a parameterised command bound in an item template
            // was evaluated with a null parameter and stayed disabled (seen in UI captures).
            SwitchCommand = new RelayCommand(async () => await owner.SwitchToAsync(this), () => !IsCurrent && !OperationGate.IsBusy);
        }

        public Rj45ViewModel Owner { get; }
        public RelayCommand SwitchCommand { get; }
        public VmInfo? Vm { get; }
        public bool IsHost => Vm == null;
        public bool IsCurrent { get; }
        public bool IsRunning => Vm?.IsRunning ?? true;

        public string Name => IsHost ? LocalizationService.Get("Str.Rj45.HostName") : Vm!.Name;

        public string Sub => IsHost
            ? LocalizationService.Format("Str.Rj45.HostSub", Owner.PhysicalName)
            : HyperVContext.Instance.State?.AdaptersOf(Vm!.Name).FirstOrDefault(Rj45Rules.IsPoniAdapter) is VmAdapterInfo mine
                ? LocalizationService.Format("Str.Rj45.VmSubAdapter", mine.Name)
                : LocalizationService.Get("Str.Rj45.VmSubNone");

        public string StateText => IsCurrent
            ? LocalizationService.Get("Str.Rj45.LinkedHere")
            : IsHost ? LocalizationService.Get("Str.Rj45.Available") : VmStateText.Of(Vm!.State);

        public string ButtonText => LocalizationService.Get(IsCurrent ? "Str.Rj45.CurrentDestination" : IsHost ? "Str.Rj45.GiveToWindows" : "Str.Rj45.LinkHere");
    }

    /// <summary>Localized Hyper-V VM state.</summary>
    public static class VmStateText
    {
        public static string Of(string state) => state switch
        {
            "Running" => LocalizationService.Get("Str.VmState.Running"),
            "Off" => LocalizationService.Get("Str.VmState.Off"),
            "Saved" => LocalizationService.Get("Str.VmState.Saved"),
            "Paused" => LocalizationService.Get("Str.VmState.Paused"),
            "Starting" => LocalizationService.Get("Str.VmState.Starting"),
            "Stopping" => LocalizationService.Get("Str.VmState.Stopping"),
            _ => state,
        };
    }

    /// <summary>
    /// "RJ45 port" screen: where the physical port points, read LIVE from Hyper-V,
    /// and one click to give it to Windows or to a VM (exclusive).
    /// </summary>
    public sealed class Rj45ViewModel : ObservableObject
    {
        private List<AdapterInfo> _hostAdapters = new List<AdapterInfo>();
        private bool _isRefreshing;

        public Rj45ViewModel()
        {
            RefreshCommand = new RelayCommand(async () => await RefreshAsync(), () => !_isRefreshing);
            SwitchCommand = new RelayCommand(async d => await SwitchAsync(d as Rj45DestinationViewModel), d => !OperationGate.IsBusy && d is Rj45DestinationViewModel dest && !dest.IsCurrent);
            ReturnToHostCommand = new RelayCommand(async () => await SwitchAsync(Destinations.FirstOrDefault(d => d.IsHost)), () => !OperationGate.IsBusy && Mode != Rj45Mode.Host);
            ChangePhysicalCommand = new RelayCommand(async () => await ChoosePhysicalAdapterAsync());
            RepairCommand = new RelayCommand(async () => await RepairAsync(), () => !OperationGate.IsBusy);
            HyperVContext.Instance.StateChanged += (_, __) => Rebuild();
            LocalizationService.LanguageChanged += (_, __) => Rebuild();
        }

        public ObservableCollection<Rj45DestinationViewModel> Destinations { get; } = new ObservableCollection<Rj45DestinationViewModel>();
        public RelayCommand RefreshCommand { get; }
        public RelayCommand SwitchCommand { get; }
        public RelayCommand ReturnToHostCommand { get; }
        public RelayCommand ChangePhysicalCommand { get; }
        public RelayCommand RepairCommand { get; }

        /// <summary>VM adapters pointing to a deleted switch: those VMs cannot start.</summary>
        public bool HasDangling => HyperVContext.Instance.State?.DanglingAdapters.Count > 0;
        public string DanglingList => string.Join("\n", (HyperVContext.Instance.State?.DanglingAdapters ?? new List<VmAdapterInfo>())
            .Select(a => "•  " + a.VMName + " — " + a.Name));

        public Rj45Status? Status => HyperVContext.Instance.Rj45;
        public Rj45Mode Mode => Status?.Mode ?? Rj45Mode.Host;
        public bool HasState => HyperVContext.Instance.State != null;
        public bool IsRefreshing => _isRefreshing || HyperVContext.Instance.IsReading;
        public string? ReadError => HasState ? null : HyperVContext.Instance.LastError;

        /// <summary>The physical adapter used as the port: the RJ45 switch's uplink, else the configured / automatic one.</summary>
        public AdapterInfo? PhysicalAdapter => (Status != null && Status.SwitchExists ? Rj45Rules.UplinkOf(Status, _hostAdapters) : null)
                                               ?? Rj45Rules.PickPhysicalAdapter(_hostAdapters, Services.AppData.Data.Settings.Rj45PhysicalAdapter);
        public string PhysicalName => PhysicalAdapter?.Name ?? LocalizationService.Get("Str.Rj45.NoPhysical");
        public string PhysicalDescription => PhysicalAdapter?.Description ?? (Status?.Switch?.Uplink ?? "");

        public bool IsVm => Mode == Rj45Mode.Vm;
        public bool IsAnomaly => Mode == Rj45Mode.Shared || Mode == Rj45Mode.Orphan;
        public string TargetName => Mode switch
        {
            Rj45Mode.Vm => Status!.CurrentVm!,
            Rj45Mode.Shared => string.Join(" + ", Status!.ConnectedVms),
            Rj45Mode.Orphan => LocalizationService.Get("Str.Rj45.Nobody"),
            _ => LocalizationService.Get("Str.Rj45.HostName"),
        };
        public string TargetSub => Mode == Rj45Mode.Vm
            ? VmStateText.Of(HyperVContext.Instance.State?.FindVm(Status!.CurrentVm!)?.State ?? "")
            : Mode == Rj45Mode.Host ? LocalizationService.Get("Str.Rj45.HostSubShort") : "";

        public string PillText => HasState
            ? LocalizationService.Format(IsAnomaly ? "Str.Rj45.PillAnomaly" : "Str.Rj45.PillOk", HyperVContext.Instance.State!.ReadAt.ToString("HH:mm:ss"))
            : "";

        public string HeroTitle => LocalizationService.Format(Mode switch
        {
            Rj45Mode.Vm => "Str.Rj45.TitleVm",
            Rj45Mode.Shared => "Str.Rj45.TitleShared",
            Rj45Mode.Orphan => "Str.Rj45.TitleOrphan",
            _ => "Str.Rj45.TitleHost",
        }, TargetName);

        public string HeroText => LocalizationService.Format(Mode switch
        {
            Rj45Mode.Vm => "Str.Rj45.TextVm",
            Rj45Mode.Shared => "Str.Rj45.TextShared",
            Rj45Mode.Orphan => "Str.Rj45.TextOrphan",
            _ => "Str.Rj45.TextHost",
        }, PhysicalName);

        public bool CanChangePhysical => Mode == Rj45Mode.Host;

        public async Task RefreshAsync()
        {
            if (_isRefreshing) return;
            // On the RJ45 screen, a switch is likely: warm the engine + Hyper-V module meanwhile.
            UiOperations.WarmUpForChanges(hyperV: true);
            _isRefreshing = true;
            OnPropertyChanged(nameof(IsRefreshing));
            try
            {
                _hostAdapters = await HostNetworkService.ReadAdaptersAsync();
            }
            catch (Exception ex)
            {
                Log.Error("RJ45 view: reading adapters failed", ex);
            }
            await HyperVContext.Instance.RefreshAsync();
            _isRefreshing = false;
            Rebuild();
        }

        private void Rebuild()
        {
            Destinations.Clear();
            var state = HyperVContext.Instance.State;
            if (state != null)
            {
                var status = Status!;
                Destinations.Add(new Rj45DestinationViewModel(this, null, status.Mode == Rj45Mode.Host));
                foreach (var vm in state.Vms)
                    Destinations.Add(new Rj45DestinationViewModel(this, vm, status.Mode == Rj45Mode.Vm && status.CurrentVm == vm.Name));
            }
            foreach (var p in new[] { nameof(Status), nameof(Mode), nameof(HasState), nameof(IsRefreshing), nameof(ReadError), nameof(PhysicalAdapter),
                                      nameof(PhysicalName), nameof(PhysicalDescription), nameof(IsVm), nameof(IsAnomaly), nameof(TargetName),
                                      nameof(TargetSub), nameof(PillText), nameof(HeroTitle), nameof(HeroText), nameof(CanChangePhysical),
                                      nameof(HasDangling), nameof(DanglingList) })
                OnPropertyChanged(p);
        }

        public Task SwitchToAsync(Rj45DestinationViewModel destination) => SwitchAsync(destination);

        private async Task RepairAsync()
        {
            if (!HasDangling || OperationGate.IsBusy) return;
            var ok = await DialogService.ConfirmAsync(
                LocalizationService.Get("Str.Rj45.RepairTitle"),
                LocalizationService.Format("Str.Rj45.RepairMessage", DanglingList),
                LocalizationService.Get("Str.Rj45.Repair"));
            if (!ok) return;
            await UiOperations.RunAsync(LocalizationService.Get("Str.Rj45.Repairing"), HyperVService.RepairDanglingAdaptersAsync);
            await RefreshAsync();
        }

        private async Task SwitchAsync(Rj45DestinationViewModel? destination)
        {
            if (destination == null || destination.IsCurrent || OperationGate.IsBusy) return;
            await RefreshAsync(); // decide on the live state
            var status = Status;
            if (status == null) return;

            if (destination.IsHost)
            {
                var ok = await DialogService.ConfirmAsync(
                    LocalizationService.Get("Str.Rj45.ToHostTitle"),
                    LocalizationService.Format("Str.Rj45.ToHostMessage", PhysicalName, TargetName),
                    LocalizationService.Get("Str.Rj45.GiveToWindows"));
                if (!ok) return;
                var name = PhysicalAdapter?.Name;
                await UiOperations.RunAsync(LocalizationService.Get("Str.Rj45.Switching"), () => HyperVService.ReturnRj45ToHostAsync(name));
            }
            else
            {
                var physical = PhysicalAdapter;
                if (physical == null)
                {
                    Toast.Show(ToastKind.Error, LocalizationService.Get("Str.Rj45.NoPhysicalError"));
                    return;
                }
                var message = LocalizationService.Format("Str.Rj45.ToVmMessage", physical.Name, destination.Name);
                if (status.Mode == Rj45Mode.Vm || status.Mode == Rj45Mode.Shared)
                    message += "\n\n" + LocalizationService.Format("Str.Rj45.ToVmLoses", string.Join(", ", status.ConnectedVms));
                if (!destination.IsRunning)
                    message += "\n\n" + LocalizationService.Get("Str.Rj45.ToVmStopped");
                var ok = await DialogService.ConfirmAsync(LocalizationService.Get("Str.Rj45.ToVmTitle"), message, LocalizationService.Get("Str.Rj45.LinkHere"));
                if (!ok) return;
                var vmName = destination.Name;
                await UiOperations.RunAsync(LocalizationService.Get("Str.Rj45.Switching"), () => HyperVService.ConnectRj45ToVmAsync(vmName, physical.Name));
            }
            await RefreshAsync();
        }

        /// <summary>The physical adapter used as the RJ45 port is a choice, not hard-coded.</summary>
        public async Task ChoosePhysicalAdapterAsync()
        {
            if (!CanChangePhysical)
            {
                Toast.Show(ToastKind.Error, LocalizationService.Get("Str.Rj45.ChangeOnlyOnHost"));
                return;
            }
            if (_hostAdapters.Count == 0) _hostAdapters = await HostNetworkService.ReadAdaptersAsync();
            var dialog = new PhysicalAdapterDialogViewModel(_hostAdapters, PhysicalAdapter?.Name);
            if (!(await DialogService.ShowAsync(dialog) is string chosen)) return;
            Services.AppData.Data.Settings.Rj45PhysicalAdapter = chosen;
            Services.AppData.Save();
            Rebuild();
            Toast.Show(ToastKind.Success, LocalizationService.Format("Str.Rj45.PhysicalChanged", chosen));
        }
    }
}
