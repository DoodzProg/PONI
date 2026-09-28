using System;
using System.Threading.Tasks;
using Poni.Core.HyperV;
using Poni.Infrastructure;

namespace Poni.Services.HyperV
{
    /// <summary>
    /// App-wide Hyper-V module state (E2 of the v2 requirements): detected availability, the
    /// user's on/off switch (Settings), and the last live Hyper-V read. When the module is off
    /// or Hyper-V is absent, the RJ45 screen and the "Virtual machine" target disappear and no
    /// Hyper-V call is ever made.
    /// </summary>
    public sealed class HyperVContext : ObservableObject
    {
        public static HyperVContext Instance { get; } = new HyperVContext();

        private HyperVAvailability _availability = HyperVAvailability.NotInstalled;
        private HyperVState? _state;
        private bool _isReading;
        private string? _lastError;

        private HyperVContext()
        {
            LocalizationService.LanguageChanged += (_, __) => RaiseTexts();
        }

        public HyperVAvailability Availability
        {
            get => _availability;
            private set { if (SetProperty(ref _availability, value)) RaiseAll(); }
        }

        /// <summary>The user's choice; null = on whenever Hyper-V is available.</summary>
        public bool? Setting => AppData.Data.Settings.HyperVEnabled;

        /// <summary>The Hyper-V features are shown and used.</summary>
        public bool IsEnabled => _availability == HyperVAvailability.Available && Setting != false;

        public bool CanToggle => _availability == HyperVAvailability.Available;

        public HyperVState? State
        {
            get => _state;
            private set { if (SetProperty(ref _state, value)) RaiseAll(); }
        }

        public Rj45Status? Rj45 => _state == null ? null : Rj45Rules.Compute(_state);

        public bool IsReading
        {
            get => _isReading;
            private set => SetProperty(ref _isReading, value);
        }

        public string? LastError
        {
            get => _lastError;
            private set => SetProperty(ref _lastError, value);
        }

        public event EventHandler? StateChanged;

        /// <summary>Sidebar chip: "Hyper-V active · 3 VMs".</summary>
        public string ChipText
        {
            get
            {
                if (_availability == HyperVAvailability.NotInstalled) return LocalizationService.Get("Str.HyperV.ChipAbsent");
                if (!IsEnabled) return LocalizationService.Get("Str.HyperV.ChipOff");
                return _state == null
                    ? LocalizationService.Get("Str.HyperV.ChipOn")
                    : LocalizationService.Format("Str.HyperV.ChipVms", _state.Vms.Count);
            }
        }

        /// <summary>Settings caption under the switch.</summary>
        public string StatusText => LocalizationService.Get(_availability switch
        {
            HyperVAvailability.NotInstalled => "Str.HyperV.StatusAbsent",
            HyperVAvailability.NoAccess => "Str.HyperV.StatusNoAccess",
            HyperVAvailability.NoModule => "Str.HyperV.StatusNoModule",
            _ => IsEnabled ? "Str.HyperV.StatusOn" : "Str.HyperV.StatusOff",
        });

        public bool IsDemo => HyperVService.IsDemo;

        /// <summary>Startup: cheap detection, then (if enabled) a first read in the background.</summary>
        public void Initialize()
        {
            Availability = HyperVService.Detect();
            Log.Info("Hyper-V: " + _availability + (IsDemo ? " (demo)" : "") + ", module " + (IsEnabled ? "on" : "off"));
            // First read through WMI (fast, no PowerShell). The Hyper-V PowerShell module is only
            // loaded when a change is about to be made (see WarmUpForChanges).
            if (IsEnabled) _ = RefreshAsync();
        }

        public void SetEnabled(bool enabled)
        {
            AppData.Data.Settings.HyperVEnabled = enabled;
            AppData.Save();
            Log.Info("Hyper-V module switched " + (enabled ? "on" : "off") + " by the user");
            RaiseAll();
            if (IsEnabled && _state == null) _ = RefreshAsync();
        }

        /// <summary>Re-reads Hyper-V (VMs, adapters, switches). Never runs when the module is off.</summary>
        public async Task RefreshAsync()
        {
            if (!IsEnabled || _isReading) return;
            IsReading = true;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var read = await HyperVService.ReadStateAsync();
                Log.Info($"Hyper-V state read in {watch.ElapsedMilliseconds} ms" + (read.State == null ? " (failed)" : ""));
                if (read.State != null)
                {
                    LastError = null;
                    State = read.State;
                }
                else
                {
                    LastError = read.Error;
                    if (read.Availability != HyperVAvailability.Available) Availability = read.Availability;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Hyper-V read failed", ex);
                LastError = ex.Message;
            }
            finally
            {
                IsReading = false;
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void RaiseAll()
        {
            OnPropertyChanged(nameof(Setting));
            OnPropertyChanged(nameof(IsEnabled));
            OnPropertyChanged(nameof(CanToggle));
            OnPropertyChanged(nameof(Rj45));
            RaiseTexts();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseTexts()
        {
            OnPropertyChanged(nameof(ChipText));
            OnPropertyChanged(nameof(StatusText));
        }
    }
}
