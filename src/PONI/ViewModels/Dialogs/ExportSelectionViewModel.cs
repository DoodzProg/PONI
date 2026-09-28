using System.Collections.Generic;
using System.Linq;
using Poni.Core;
using Poni.Infrastructure;
using Poni.Services;

namespace Poni.ViewModels
{
    public sealed class ExportChoice : ObservableObject
    {
        private bool _isChecked;

        public ExportChoice(NetworkProfile profile, int index)
        {
            Profile = profile;
            AvatarIndex = index % 4;
        }

        public NetworkProfile Profile { get; }
        public int AvatarIndex { get; }
        public string Name => Profile.Name;
        public string Initial => ProfileText.Initial(Profile.Name);
        public string Meta => ProfileText.Meta(Profile);
        public bool IsChecked { get => _isChecked; set => SetProperty(ref _isChecked, value); }
    }

    /// <summary>
    /// "Export a selection": tick the profiles to export (v1 had it, the first v2 build only
    /// offered "all" or "one"; user feedback, 2026-09-28). Result: the chosen profiles.
    /// </summary>
    public sealed class ExportSelectionViewModel : DialogViewModel
    {
        public ExportSelectionViewModel(IEnumerable<NetworkProfile> profiles)
        {
            Choices = profiles.Select((p, i) => new ExportChoice(p, i)).ToList();
            foreach (var c in Choices) c.PropertyChanged += (_, __) => Refresh();
            ExportCommand = new RelayCommand(() => Close(Choices.Where(c => c.IsChecked).Select(c => c.Profile).ToList()), () => SelectedCount > 0);
            ToggleAllCommand = new RelayCommand(() =>
            {
                bool check = !AllChecked;
                foreach (var c in Choices) c.IsChecked = check;
            });
        }

        public IReadOnlyList<ExportChoice> Choices { get; }
        public int SelectedCount => Choices.Count(c => c.IsChecked);
        public bool AllChecked => Choices.Count > 0 && Choices.All(c => c.IsChecked);
        public string ToggleAllText => LocalizationService.Get(AllChecked ? "Str.Transfer.SelectNone" : "Str.Transfer.SelectAll");
        public string ExportText => LocalizationService.Format("Str.Transfer.ExportCount", SelectedCount);

        public RelayCommand ExportCommand { get; }
        public RelayCommand ToggleAllCommand { get; }

        public override double DialogWidth => 520;

        private void Refresh()
        {
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(AllChecked));
            OnPropertyChanged(nameof(ToggleAllText));
            OnPropertyChanged(nameof(ExportText));
        }
    }
}
