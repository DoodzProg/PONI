using System;
using System.Windows.Threading;
using Poni.Infrastructure;
using Poni.Services;
using Poni.Services.HyperV;

namespace Poni.ViewModels
{
    public enum AppPage { Profiles, Rj45, Settings }

    /// <summary>Root view model: navigation, the page shown, the in-window dialog and the toast.</summary>
    public sealed class MainViewModel : ObservableObject
    {
        private static readonly TimeSpan RefreshDebounce = TimeSpan.FromSeconds(3);

        private AppPage _currentPage = AppPage.Profiles;
        private DialogViewModel? _dialog;
        private string? _toastText;
        private ToastKind _toastKind;
        private bool _toastVisible;
        private readonly DispatcherTimer _toastTimer;
        private DateTime _lastActivatedRefresh = DateTime.MinValue;

        public MainViewModel()
        {
            DialogService.Host = this;
            _toastTimer = new DispatcherTimer();
            _toastTimer.Tick += (_, __) => { _toastTimer.Stop(); ToastVisible = false; };
            CloseToastCommand = new RelayCommand(() => ToastVisible = false);
            // Toast "Log" button: the in-app viewer; it can open the folder itself.
            OpenLogsCommand = new RelayCommand(async () => await LogViewerViewModel.ShowAsync());
            OpenSettingsCommand = new RelayCommand(() => CurrentPage = AppPage.Settings);
            ToggleSidebarCommand = new RelayCommand(() => IsSidebarCollapsed = !IsSidebarCollapsed);
            LocalizationService.LanguageChanged += (_, __) => OnPropertyChanged(nameof(SidebarToggleText));
            Rj45 = new Rj45ViewModel();
            Settings = new SettingsViewModel(Rj45);
            // Hyper-V module switched off (or not readable) while its screen is shown: leave it.
            HyperV.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(HyperVContext.IsEnabled) && !HyperV.IsEnabled && _currentPage == AppPage.Rj45)
                    CurrentPage = AppPage.Profiles;
            };
        }

        public ProfilesViewModel Profiles { get; } = new ProfilesViewModel();
        public Rj45ViewModel Rj45 { get; }
        public SettingsViewModel Settings { get; }

        /// <summary>Hyper-V module: availability, on/off, live state (RJ45 screen, VM target, sidebar chip).</summary>
        public HyperVContext HyperV => HyperVContext.Instance;
        public RelayCommand OpenSettingsCommand { get; }

        public AppPage CurrentPage
        {
            get => _currentPage;
            set
            {
                if (!SetProperty(ref _currentPage, value)) return;
                OnPropertyChanged(nameof(CurrentPageViewModel));
                if (value == AppPage.Profiles) _ = Profiles.RefreshAdaptersAsync();
                if (value == AppPage.Rj45) _ = Rj45.RefreshAsync();
            }
        }

        /// <summary>The page view model; MainWindow maps each type to its view through DataTemplates.</summary>
        public object CurrentPageViewModel => _currentPage switch
        {
            AppPage.Rj45 => Rj45,
            AppPage.Settings => Settings,
            _ => Profiles,
        };

        public string VersionBadge => AppInfo.ShortVersion;

        // ------------------------------------------------------------ sidebar

        /// <summary>Sidebar folded to icons only (chevron / Ctrl+B), remembered in store.json.</summary>
        public bool IsSidebarCollapsed
        {
            get => AppData.Data.Settings.SidebarCollapsed;
            set
            {
                if (value == AppData.Data.Settings.SidebarCollapsed) return;
                AppData.Data.Settings.SidebarCollapsed = value;
                AppData.Save();
                OnPropertyChanged();
                OnPropertyChanged(nameof(SidebarToggleText));
            }
        }

        public string SidebarToggleText => LocalizationService.Get(IsSidebarCollapsed ? "Str.Nav.Expand" : "Str.Nav.Collapse");
        public RelayCommand ToggleSidebarCommand { get; }

        public RelayCommand OpenRepositoryCommand { get; } = new RelayCommand(() => AppInfo.OpenUrl(AppInfo.RepositoryUrl));
        public RelayCommand OpenAuthorCommand { get; } = new RelayCommand(() => AppInfo.OpenUrl(AppInfo.AuthorUrl));

        // ------------------------------------------------------------ dialog

        public DialogViewModel? Dialog
        {
            get => _dialog;
            set
            {
                if (!SetProperty(ref _dialog, value)) return;
                OnPropertyChanged(nameof(HasDialog));
                OnPropertyChanged(nameof(IsPageInteractive));
            }
        }

        public bool HasDialog => _dialog != null;

        /// <summary>False while a dialog is open: the page and sidebar underneath must not be
        /// reachable by keyboard (Tab) or accessibility tools either, not only by the mouse.</summary>
        public bool IsPageInteractive => _dialog == null;

        // ------------------------------------------------------------ toast

        public string? ToastText { get => _toastText; private set => SetProperty(ref _toastText, value); }
        public ToastKind ToastKind { get => _toastKind; private set => SetProperty(ref _toastKind, value); }
        public bool ToastVisible { get => _toastVisible; private set => SetProperty(ref _toastVisible, value); }
        public string ToastTime { get; private set; } = "";
        public RelayCommand CloseToastCommand { get; }
        public RelayCommand OpenLogsCommand { get; }

        /// <summary>Busy stays until replaced; success fades after 6 s; errors stay 15 s (time to read).</summary>
        public void ShowToast(ToastKind kind, string message)
        {
            _toastTimer.Stop();
            ToastKind = kind;
            ToastText = message;
            ToastTime = DateTime.Now.ToString("HH:mm:ss");
            OnPropertyChanged(nameof(ToastTime));
            ToastVisible = true;
            if (kind == ToastKind.Busy) return;
            _toastTimer.Interval = TimeSpan.FromSeconds(kind == ToastKind.Error ? 15 : 6);
            _toastTimer.Start();
        }

        public void HideBusyToast()
        {
            if (ToastKind == ToastKind.Busy) ToastVisible = false;
        }

        // ------------------------------------------------------------ lifecycle

        public void OnStarted() => _ = Profiles.RefreshAdaptersAsync();

        /// <summary>
        /// PONI regains focus: the network may have changed meanwhile. Debounced (v1
        /// re-read everything on every activation, including after closing each dialog).
        /// </summary>
        public void OnActivated()
        {
            if (_currentPage != AppPage.Profiles || HasDialog || OperationGate.IsBusy) return;
            if (DateTime.Now - _lastActivatedRefresh < RefreshDebounce) return;
            _lastActivatedRefresh = DateTime.Now;
            _ = Profiles.RefreshAdaptersAsync();
        }
    }
}
