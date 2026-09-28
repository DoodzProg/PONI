using System.Collections.Generic;
using System.Linq;
using Poni.Core.HyperV;
using Poni.Core.Network;
using Poni.Infrastructure;
using Poni.Services;

namespace Poni.ViewModels
{
    public sealed class PhysicalAdapterChoice : ObservableObject
    {
        private bool _isSelected;

        public PhysicalAdapterChoice(AdapterInfo adapter)
        {
            Adapter = adapter;
            IsAvailable = Rj45Rules.IsCandidate(adapter) && adapter.IsEnabled;
        }

        public AdapterInfo Adapter { get; }
        public string Name => Adapter.Name;
        public bool IsAvailable { get; }
        public string Detail => Adapter.Kind == AdapterKind.WiFi
            ? LocalizationService.Get("Str.Rj45.WifiNotSuitable")
            : !Adapter.IsEnabled
                ? LocalizationService.Get("Str.Adapter.Disabled")
                : Adapter.Description + " · " + LocalizationService.Get(Adapter.IsConnected ? "Str.Adapter.Connected" : "Str.Adapter.Unplugged");
        public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    }

    /// <summary>Which physical adapter is the RJ45 port. Result: the adapter name.</summary>
    public sealed class PhysicalAdapterDialogViewModel : DialogViewModel
    {
        public PhysicalAdapterDialogViewModel(IEnumerable<AdapterInfo> adapters, string? current)
        {
            Choices = adapters.Where(a => !a.IsVirtual).Select(a => new PhysicalAdapterChoice(a)).ToList();
            var pick = Choices.FirstOrDefault(c => c.IsAvailable && c.Name == current) ?? Choices.FirstOrDefault(c => c.IsAvailable);
            if (pick != null) pick.IsSelected = true;
            ConfirmCommand = new RelayCommand(() =>
            {
                var chosen = Choices.FirstOrDefault(c => c.IsSelected && c.IsAvailable);
                if (chosen != null) Close(chosen.Name);
            });
        }

        public IReadOnlyList<PhysicalAdapterChoice> Choices { get; }
        public RelayCommand ConfirmCommand { get; }
        public override double DialogWidth => 480;
    }
}
