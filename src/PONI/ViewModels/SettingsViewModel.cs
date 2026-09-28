using Poni.Core;
using Poni.Infrastructure;
using Poni.Services;
using Poni.Services.HyperV;

namespace Poni.ViewModels
{
    /// <summary>
    /// "Settings" page. Changes apply instantly (ThemeService / LocalizationService) and are
    /// saved right away in store.json.
    /// </summary>
    public sealed class SettingsViewModel : ObservableObject
    {
        private static AppSettings Stored => AppData.Data.Settings;
        private readonly Rj45ViewModel _rj45;

        public SettingsViewModel(Rj45ViewModel rj45)
        {
            _rj45 = rj45;
            ChangePhysicalCommand = new RelayCommand(async () => { await _rj45.RefreshAsync(); await _rj45.ChoosePhysicalAdapterAsync(); OnPropertyChanged(nameof(PhysicalName)); });
            HyperV.PropertyChanged += (_, __) => OnPropertyChanged(nameof(HyperVEnabled));
            _rj45.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Rj45ViewModel.PhysicalName)) OnPropertyChanged(nameof(PhysicalName)); };
        }

        // ------------------------------------------------------------ Hyper-V module (E2)

        public HyperVContext HyperV => HyperVContext.Instance;

        /// <summary>The Settings switch. Only effective when Hyper-V is available (CanToggle).</summary>
        public bool HyperVEnabled
        {
            get => HyperV.IsEnabled;
            set
            {
                if (value == HyperV.IsEnabled || !HyperV.CanToggle) return;
                HyperV.SetEnabled(value);
                OnPropertyChanged();
            }
        }

        public string PhysicalName => _rj45.PhysicalName;
        public RelayCommand ChangePhysicalCommand { get; }

        public AppLanguage Language
        {
            get => LocalizationService.Language;
            set
            {
                if (value == LocalizationService.Language) return;
                LocalizationService.SetLanguage(value);
                Stored.Language = value == AppLanguage.French ? "fr" : "en";
                AppData.Save();
                OnPropertyChanged();
            }
        }

        public ThemePreference Theme
        {
            get => ThemeService.Mode;
            set
            {
                if (value == ThemeService.Mode) return;
                ThemeService.SetMode(value);
                Stored.Theme = value;
                AppData.Save();
                OnPropertyChanged();
            }
        }

        /// <summary>v1 forced "Private" on every apply, silently. Now opt-in, off by default.</summary>
        public bool SetPrivateOnApply
        {
            get => Stored.SetPrivateOnApply;
            set
            {
                if (value == Stored.SetPrivateOnApply) return;
                Stored.SetPrivateOnApply = value;
                AppData.Save();
                OnPropertyChanged();
            }
        }

        /// <summary>Restore the previous configuration when applying fails. On by default.</summary>
        public bool RollbackOnFailure
        {
            get => Stored.RollbackOnFailure;
            set
            {
                if (value == Stored.RollbackOnFailure) return;
                Stored.RollbackOnFailure = value;
                AppData.Save();
                OnPropertyChanged();
            }
        }

        /// <summary>Applying in a VM also lets it answer ping from the local network (PONI's own guest firewall rule). On by default.</summary>
        public bool VmAllowPing
        {
            get => Stored.VmAllowPing;
            set
            {
                if (value == Stored.VmAllowPing) return;
                Stored.VmAllowPing = value;
                AppData.Save();
                OnPropertyChanged();
            }
        }

        public RelayCommand OpenLogCommand { get; } = new RelayCommand(async () => await LogViewerViewModel.ShowAsync());

        public string VersionLabel => "PONI " + AppInfo.FullVersion;

        public RelayCommand OpenRepositoryCommand { get; } = new RelayCommand(() => AppInfo.OpenUrl(AppInfo.RepositoryUrl));
    }
}
