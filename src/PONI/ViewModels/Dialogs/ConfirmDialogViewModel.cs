using Poni.Infrastructure;

namespace Poni.ViewModels
{
    /// <summary>Yes / Cancel question. Result: true, or null when cancelled.</summary>
    public sealed class ConfirmDialogViewModel : DialogViewModel
    {
        public ConfirmDialogViewModel(string title, string message, string confirmText, bool isDestructive)
        {
            Title = title;
            Message = message;
            ConfirmText = confirmText;
            IsDestructive = isDestructive;
            ConfirmCommand = new RelayCommand(() => Close(true));
        }

        public string Title { get; }
        public string Message { get; }
        public string ConfirmText { get; }
        /// <summary>Red confirm button (delete...).</summary>
        public bool IsDestructive { get; }
        public RelayCommand ConfirmCommand { get; }

        public override double DialogWidth => 420;
    }
}
