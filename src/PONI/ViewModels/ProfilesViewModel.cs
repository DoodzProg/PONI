using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using Poni.Core;
using Poni.Core.Network;
using Poni.Infrastructure;
using Poni.Services;
using Poni.Services.HyperV;
using Poni.Services.Network;

namespace Poni.ViewModels
{
    /// <summary>
    /// "Network profiles" page: live adapters ("This machine"), saved profiles, and every action
    /// on them (create / edit / duplicate / delete / apply / import / export, DHCP, category).
    /// </summary>
    public sealed class ProfilesViewModel : ObservableObject
    {
        private List<AdapterInfo> _adapters = new List<AdapterInfo>();
        private string _search = "";
        private bool _showVirtual;
        private bool _isRefreshing;
        private bool _preparingApply;
        private DateTime? _lastRefresh;

        public ProfilesViewModel()
        {
            RefreshCommand = new RelayCommand(async () => await RefreshAdaptersAsync(), () => !_isRefreshing);
            NewProfileCommand = new RelayCommand(async () => await NewProfileAsync());
            EditCommand = new RelayCommand(async p => await EditAsync(p as ProfileRowViewModel, EditorMode.Edit));
            DuplicateCommand = new RelayCommand(async p => await EditAsync(p as ProfileRowViewModel, EditorMode.Duplicate));
            DeleteCommand = new RelayCommand(async p => await DeleteAsync(p as ProfileRowViewModel));
            ApplyCommand = new RelayCommand(async p => await ApplyAsync(p as ProfileRowViewModel), _ => !OperationGate.IsBusy);
            ExportOneCommand = new RelayCommand(p =>
            {
                if (p is ProfileRowViewModel row) Export(new List<NetworkProfile> { row.Profile }, SafeFileName(row.Profile.Name) + ".json");
            });
            ExportAllCommand = new RelayCommand(() => Export(AppData.Data.Profiles.ToList(), "poni-profiles.json"),
                                                () => AppData.Data.Profiles.Count > 0);
            ExportSelectionCommand = new RelayCommand(async () => await ExportSelectionAsync(), () => AppData.Data.Profiles.Count > 0);
            ImportCommand = new RelayCommand(Import);
            ToggleVirtualCommand = new RelayCommand(() => { _showVirtual = !_showVirtual; RebuildAdapters(); });
            ConfigureAdapterCommand = new RelayCommand(async a => await ConfigureAdapterAsync(a as AdapterCardViewModel), _ => !OperationGate.IsBusy);
            DhcpCommand = new RelayCommand(async a => await DhcpAsync(a as AdapterCardViewModel), _ => !OperationGate.IsBusy);
            MakePrivateCommand = new RelayCommand(async a => await CategoryAsync(a as AdapterCardViewModel, NetworkCategory.Private), _ => !OperationGate.IsBusy);
            MakePublicCommand = new RelayCommand(async a => await CategoryAsync(a as AdapterCardViewModel, NetworkCategory.Public), _ => !OperationGate.IsBusy);

            MoveUpCommand = new RelayCommand(p => Step(p as ProfileRowViewModel, -1), _ => CanReorder);
            MoveDownCommand = new RelayCommand(p => Step(p as ProfileRowViewModel, +1), _ => CanReorder);

            LocalizationService.LanguageChanged += (_, __) => { RebuildAdapters(); RaiseSortTexts(); RebuildRows(); };
            RaiseSortTexts();
            RebuildRows();
        }

        // ------------------------------------------------------------ bindable state

        public ObservableCollection<AdapterCardViewModel> Adapters { get; } = new ObservableCollection<AdapterCardViewModel>();
        public ObservableCollection<ProfileRowViewModel> Rows { get; } = new ObservableCollection<ProfileRowViewModel>();

        public string Search
        {
            get => _search;
            set { if (SetProperty(ref _search, value)) RebuildRows(); }
        }

        public bool DetailedView
        {
            get => AppData.Data.Settings.DetailedView;
            set
            {
                if (value == AppData.Data.Settings.DetailedView) return;
                AppData.Data.Settings.DetailedView = value;
                AppData.Save();
                OnPropertyChanged();
                OnPropertyChanged(nameof(SimpleView));
            }
        }

        public bool SimpleView
        {
            get => !DetailedView;
            set { if (value) DetailedView = false; }
        }

        public int ProfileCount => AppData.Data.Profiles.Count;
        public bool HasProfiles => ProfileCount > 0;
        public bool NoSearchResult => HasProfiles && Rows.Count == 0;
        public bool IsRefreshing => _isRefreshing;

        public int HiddenVirtualCount => _adapters.Count(a => a.IsVirtual && NetworkRules.IsWorthListing(a));
        public bool HasVirtualAdapters => HiddenVirtualCount > 0;
        public string VirtualToggleText => LocalizationService.Format(_showVirtual ? "Str.Machine.HideVirtual" : "Str.Machine.ShowVirtual", HiddenVirtualCount);

        public string RefreshText => _lastRefresh == null
            ? LocalizationService.Get("Str.Machine.Reading")
            : LocalizationService.Format("Str.Machine.RefreshedAt", _lastRefresh.Value.ToString("HH:mm:ss"));

        // ------------------------------------------------------------ commands

        public RelayCommand RefreshCommand { get; }
        public RelayCommand NewProfileCommand { get; }
        public RelayCommand EditCommand { get; }
        public RelayCommand DuplicateCommand { get; }
        public RelayCommand DeleteCommand { get; }
        public RelayCommand ApplyCommand { get; }
        public RelayCommand ExportOneCommand { get; }
        public RelayCommand ExportAllCommand { get; }
        public RelayCommand ExportSelectionCommand { get; }
        public RelayCommand ImportCommand { get; }
        public RelayCommand ToggleVirtualCommand { get; }
        public RelayCommand ConfigureAdapterCommand { get; }
        public RelayCommand DhcpCommand { get; }
        public RelayCommand MakePrivateCommand { get; }
        public RelayCommand MakePublicCommand { get; }

        // ------------------------------------------------------------ adapters

        /// <summary>Re-reads the adapters (WMI, background thread). Event-driven, never polled.</summary>
        public async Task RefreshAdaptersAsync()
        {
            if (_isRefreshing) return;
            _isRefreshing = true;
            OnPropertyChanged(nameof(IsRefreshing));
            try
            {
                _adapters = await HostNetworkService.ReadAdaptersAsync();
                _lastRefresh = DateTime.Now;
            }
            catch (Exception ex)
            {
                Log.Error("Reading the network adapters failed", ex);
                Toast.Show(ToastKind.Error, LocalizationService.Get("Str.Machine.ReadFailed"));
            }
            finally
            {
                _isRefreshing = false;
                OnPropertyChanged(nameof(IsRefreshing));
                RebuildAdapters();
                RebuildRows(); // the "Active" badges depend on the live configuration
            }
        }

        private void RebuildAdapters()
        {
            Adapters.Clear();
            foreach (var a in _adapters.Where(a => _showVirtual ? NetworkRules.IsWorthListing(a) : NetworkRules.IsShownByDefault(a)))
                Adapters.Add(new AdapterCardViewModel(this, a));
            OnPropertyChanged(nameof(HiddenVirtualCount));
            OnPropertyChanged(nameof(HasVirtualAdapters));
            OnPropertyChanged(nameof(VirtualToggleText));
            OnPropertyChanged(nameof(RefreshText));
        }

        // ------------------------------------------------------------ profiles

        private void RebuildRows()
        {
            Rows.Clear();
            var profiles = AppData.Data.Profiles;
            int shown = 0;
            foreach (var p in ProfileOrder.Apply(profiles, Sort))
            {
                var active = _adapters.FirstOrDefault(a => NetworkRules.Matches(p, a));
                // Avatar colour follows the stored position: it does not change when re-sorting.
                var row = new ProfileRowViewModel(this, p, profiles.IndexOf(p), active, shown == 0);
                if (!row.Matches(_search)) continue;
                Rows.Add(row);
                shown++;
            }
            OnPropertyChanged(nameof(ProfileCount));
            OnPropertyChanged(nameof(HasProfiles));
            OnPropertyChanged(nameof(NoSearchResult));
            OnPropertyChanged(nameof(CanReorder));
        }

        // ------------------------------------------------------------ sort / custom order

        public ProfileSort Sort
        {
            get => AppData.Data.Settings.ProfileSort;
            set
            {
                if (value == AppData.Data.Settings.ProfileSort) return;
                AppData.Data.Settings.ProfileSort = value;
                AppData.Save();
                OnPropertyChanged();
                RaiseSortTexts();
                RebuildRows();
            }
        }

        /// <summary>The funnel menu: one entry per sort, the current one ticked.</summary>
        public IReadOnlyList<SortChoice> SortChoices { get; private set; } = Array.Empty<SortChoice>();

        public string SortTooltip => LocalizationService.Format("Str.Sort.Tooltip", LocalizationService.Get(SortChoice.KeyOf(Sort)));

        /// <summary>Drag handles and "Move up / down": custom order, full list (not while searching).</summary>
        public bool CanReorder => Sort == ProfileSort.Custom && string.IsNullOrWhiteSpace(_search) && AppData.Data.Profiles.Count > 1;

        public RelayCommand MoveUpCommand { get; }
        public RelayCommand MoveDownCommand { get; }

        private void RaiseSortTexts()
        {
            SortChoices = Enum.GetValues(typeof(ProfileSort)).Cast<ProfileSort>()
                .OrderBy(s => s == ProfileSort.Custom ? 1 : 0) // "Custom" last in the menu
                .Select(s => new SortChoice(this, s)).ToList();
            OnPropertyChanged(nameof(SortChoices));
            OnPropertyChanged(nameof(SortTooltip));
        }

        /// <summary>Drag and drop: <paramref name="moved"/> lands above / below <paramref name="target"/>.</summary>
        public void Drop(ProfileRowViewModel moved, ProfileRowViewModel target, bool below)
        {
            if (!CanReorder || !ProfileOrder.Move(AppData.Data.Profiles, moved.Profile, target.Profile, below)) return;
            SaveOrder(moved.Profile);
        }

        private void Step(ProfileRowViewModel? row, int delta)
        {
            if (row == null || !CanReorder || !ProfileOrder.Step(AppData.Data.Profiles, row.Profile, delta)) return;
            SaveOrder(row.Profile);
        }

        private void SaveOrder(NetworkProfile moved)
        {
            RebuildRows();
            if (!AppData.Save()) Toast.Show(ToastKind.Error, LocalizationService.Get("Str.Store.SaveFailed"));
            Log.Info($"Profile '{moved.Name}' moved to position {AppData.Data.Profiles.IndexOf(moved) + 1}");
        }

        private IEnumerable<string> ExistingNames => AppData.Data.Profiles.Select(p => p.Name);

        private async Task NewProfileAsync()
        {
            var editor = new ProfileEditorViewModel(EditorMode.New, null, ExistingNames, _adapters);
            if (await DialogService.ShowAsync(editor) is NetworkProfile created)
            {
                created.CreatedOn = DateTime.Now;
                AppData.Data.Profiles.Add(created);
                SaveAndRefresh("Str.Profile.Created", created.Name);
            }
        }

        private async Task EditAsync(ProfileRowViewModel? row, EditorMode mode)
        {
            if (row == null) return;
            var editor = new ProfileEditorViewModel(mode, row.Profile, ExistingNames, _adapters);
            if (!(await DialogService.ShowAsync(editor) is NetworkProfile result)) return;

            if (mode == EditorMode.Duplicate)
            {
                result.CreatedOn = DateTime.Now;
                var index = AppData.Data.Profiles.IndexOf(row.Profile);
                AppData.Data.Profiles.Insert(index + 1, result);
                SaveAndRefresh("Str.Profile.Created", result.Name);
                return;
            }

            // Editing keeps the history (v1 lost the last-applied date here).
            result.CreatedOn = row.Profile.CreatedOn;
            result.LastTarget = row.Profile.LastTarget;
            var i = AppData.Data.Profiles.IndexOf(row.Profile);
            if (i >= 0) AppData.Data.Profiles[i] = result;
            SaveAndRefresh("Str.Profile.Updated", result.Name);
        }

        private async Task DeleteAsync(ProfileRowViewModel? row)
        {
            if (row == null) return;
            var ok = await DialogService.ConfirmAsync(
                LocalizationService.Get("Str.Profile.DeleteTitle"),
                LocalizationService.Format("Str.Profile.DeleteMessage", row.Name),
                LocalizationService.Get("Str.Common.Delete"), destructive: true);
            if (!ok) return;
            AppData.Data.Profiles.Remove(row.Profile);
            SaveAndRefresh("Str.Profile.Deleted", row.Name);
        }

        private void SaveAndRefresh(string messageKey, params object[] args)
        {
            RebuildRows();
            if (AppData.Save()) Toast.Show(ToastKind.Success, LocalizationService.Format(messageKey, args));
            else Toast.Show(ToastKind.Error, LocalizationService.Get("Str.Store.SaveFailed"));
        }

        // ------------------------------------------------------------ apply

        private async Task ApplyAsync(ProfileRowViewModel? row)
        {
            if (row == null || OperationGate.IsBusy || _preparingApply) return;
            // Decide on the live state, not on a stale view. Both reads run in parallel, with
            // immediate feedback: the dialog took a while to appear with Hyper-V on (seen in real-hardware testing).
            var hyperV = HyperVContext.Instance;
            UiOperations.WarmUpForChanges(hyperV: true);
            _preparingApply = true;
            Toast.Show(ToastKind.Busy, LocalizationService.Get("Str.Apply.Preparing"));
            try
            {
                await Task.WhenAll(RefreshAdaptersAsync(), hyperV.IsEnabled ? hyperV.RefreshAsync() : Task.CompletedTask);
            }
            finally
            {
                _preparingApply = false;
                Toast.Hide();
            }

            var settings = AppData.Data.Settings;
            var dialog = new ApplyDialogViewModel(row.Profile, _adapters, settings.RollbackOnFailure, hyperV);
            if (!(await DialogService.ShowAsync(dialog) is ApplyTarget target)) return;
            var profile = row.Profile;
            var config = Ipv4Config.FromProfile(profile);

            // ---------- inside a VM (PowerShell Direct)
            if (target.VmName != null && target.VmAdapter != null)
            {
                settings.VmUsers.TryGetValue(target.VmName, out var lastUser);
                var credentials = new VmCredentialsViewModel(target.VmName, lastUser);
                if (!(await DialogService.ShowAsync(credentials) is VmCredentialsViewModel.Result login)) return;

                var vmName = target.VmName;
                var vmAdapter = target.VmAdapter;
                await UiOperations.RunAsync(
                    LocalizationService.Format("Str.Vm.Applying", profile.Name, vmName),
                    () => HyperVService.ApplyInVmAsync(vmName, vmAdapter, login.User, login.Password, config, settings.RollbackOnFailure, settings.VmAllowPing),
                    onSuccess: () =>
                    {
                        profile.LastTarget = new LastTarget { Kind = TargetKind.VM, VM = vmName, VMAdapter = vmAdapter.Name, AppliedOn = DateTime.Now };
                        settings.VmUsers[vmName] = login.User; // only once it WORKED
                        AppData.Save();
                    });
                login.Password.Dispose();
                RebuildRows();
                return;
            }

            // ---------- on the host
            var adapter = target.HostAdapter!;
            Func<Task<NetworkOperationResult>> change = () => HostNetworkService.ApplyAsync(adapter, config, settings.SetPrivateOnApply, settings.RollbackOnFailure);

            // v1 bug #2: the adapter is the RJ45 port, currently given to a VM (no IPv4 stack on the
            // host). Offer to give the port back to Windows first, then apply - in one operation.
            if (target.ReleaseRj45First)
            {
                var ok = await DialogService.ConfirmAsync(
                    LocalizationService.Get("Str.Rj45.ReleaseTitle"),
                    LocalizationService.Format("Str.Rj45.ReleaseMessage", adapter.Name),
                    LocalizationService.Get("Str.Rj45.ReleaseConfirm"));
                if (!ok) return;
                change = async () =>
                {
                    var back = await HyperVService.ReturnRj45ToHostAsync(adapter.Name);
                    if (!back.Success) return back;
                    var fresh = (await HostNetworkService.ReadAdaptersAsync()).FirstOrDefault(a => a.Name == adapter.Name);
                    if (fresh == null || !fresh.HasIpv4)
                        return new NetworkOperationResult { MessageKey = "Str.Rj45.NotBackYet", Args = new object[] { adapter.Name } };
                    return await HostNetworkService.ApplyAsync(fresh, config, settings.SetPrivateOnApply, settings.RollbackOnFailure);
                };
            }

            await RunNetworkChangeAsync(
                LocalizationService.Format("Str.Net.Applying", profile.Name, adapter.Name),
                change,
                onSuccess: () =>
                {
                    // Recorded only once it really worked (v1 recorded it before applying).
                    profile.LastTarget = new LastTarget { Kind = TargetKind.Host, Adapter = adapter.Name, AppliedOn = DateTime.Now };
                    AppData.Save();
                });
            if (target.ReleaseRj45First) await hyperV.RefreshAsync();
        }

        private async Task ConfigureAdapterAsync(AdapterCardViewModel? card)
        {
            if (card == null || OperationGate.IsBusy) return;
            UiOperations.WarmUpForChanges();
            var a = card.Adapter;
            var current = new NetworkProfile
            {
                Name = a.Name,
                IPAddress = a.PrimaryAddress?.Address ?? "",
                PrefixLength = a.PrimaryAddress?.PrefixLength ?? 24,
                Gateway = a.PrimaryGateway,
                Dns = a.DnsIsStatic ? new List<string>(a.DnsServers) : new List<string>(),
            };
            var editor = new ProfileEditorViewModel(EditorMode.ConfigureAdapter, a.HasRealAddress ? current : null,
                                                    ExistingNames, _adapters, a.Name) { ShowRiskWarning = NetworkRules.IsRisky(a) };
            if (!(await DialogService.ShowAsync(editor) is NetworkProfile typed)) return;

            await RunNetworkChangeAsync(
                LocalizationService.Format("Str.Net.ApplyingAdapter", a.Name),
                () => HostNetworkService.ApplyAsync(a, Ipv4Config.FromProfile(typed),
                        AppData.Data.Settings.SetPrivateOnApply, AppData.Data.Settings.RollbackOnFailure));
        }

        private async Task DhcpAsync(AdapterCardViewModel? card)
        {
            if (card == null || OperationGate.IsBusy) return;
            UiOperations.WarmUpForChanges();
            var ok = await DialogService.ConfirmAsync(
                LocalizationService.Get("Str.Net.DhcpTitle"),
                LocalizationService.Format("Str.Net.DhcpMessage", card.Name),
                LocalizationService.Get("Str.Net.DhcpConfirm"));
            if (!ok) return;
            await RunNetworkChangeAsync(LocalizationService.Format("Str.Net.DhcpWorking", card.Name),
                () => HostNetworkService.ResetToDhcpAsync(card.Adapter));
        }

        private async Task CategoryAsync(AdapterCardViewModel? card, NetworkCategory category)
        {
            if (card == null || OperationGate.IsBusy) return;
            await RunNetworkChangeAsync(LocalizationService.Format("Str.Net.CategoryWorking", card.Name),
                () => HostNetworkService.SetCategoryAsync(card.Adapter, category));
        }

        /// <summary>Host network change (shared frame in UiOperations), then re-read the adapters.</summary>
        private async Task RunNetworkChangeAsync(string busyText, Func<Task<NetworkOperationResult>> change, Action? onSuccess = null)
        {
            var result = await UiOperations.RunAsync(busyText, change, onSuccess);
            if (result != null) await RefreshAdaptersAsync();
        }

        // ------------------------------------------------------------ import / export

        private async Task ExportSelectionAsync()
        {
            if (!(await DialogService.ShowAsync(new ExportSelectionViewModel(AppData.Data.Profiles)) is List<NetworkProfile> chosen)) return;
            Export(chosen, chosen.Count == 1 ? SafeFileName(chosen[0].Name) + ".json" : "poni-profiles.json");
        }

        private void Export(List<NetworkProfile> profiles, string suggestedFileName)
        {
            if (profiles.Count == 0) return;
            var dialog = new SaveFileDialog
            {
                Title = LocalizationService.Get("Str.Transfer.ExportTitle"),
                Filter = "JSON (*.json)|*.json",
                FileName = suggestedFileName,
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                File.WriteAllText(dialog.FileName, StoreSerializer.Export(profiles), new UTF8Encoding(false));
                Toast.Show(ToastKind.Success, LocalizationService.Format("Str.Transfer.Exported", profiles.Count, Path.GetFileName(dialog.FileName)));
            }
            catch (Exception ex)
            {
                Log.Error("Export failed", ex);
                Toast.Show(ToastKind.Error, LocalizationService.Format("Str.Transfer.ExportFailed", ex.Message));
            }
        }

        private void Import()
        {
            var dialog = new OpenFileDialog
            {
                Title = LocalizationService.Get("Str.Transfer.ImportTitle"),
                Filter = "JSON (*.json)|*.json|*.*|*.*",
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                var read = StoreSerializer.Import(File.ReadAllText(dialog.FileName));
                var merge = ProfileMerge.AddImported(AppData.Data, read.Profiles);
                foreach (var s in read.Skipped.Concat(merge.Skipped)) Log.Warn("Import: skipped " + s);
                RebuildRows();
                AppData.Save();
                int skipped = read.Skipped.Count + merge.Skipped.Count;
                if (merge.Added == 0 && skipped == 0)
                    Toast.Show(ToastKind.Error, LocalizationService.Get("Str.Transfer.NothingFound"));
                else
                    Toast.Show(merge.Added > 0 ? ToastKind.Success : ToastKind.Error,
                        LocalizationService.Format("Str.Transfer.Imported", merge.Added, skipped));
            }
            catch (FormatException)
            {
                Toast.Show(ToastKind.Error, LocalizationService.Get("Str.Transfer.NotAProfileFile"));
            }
            catch (Exception ex)
            {
                Log.Error("Import failed", ex);
                Toast.Show(ToastKind.Error, LocalizationService.Format("Str.Transfer.ImportFailed", ex.Message));
            }
        }

        private static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }
    }
}
