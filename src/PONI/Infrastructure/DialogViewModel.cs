using System.Threading.Tasks;

namespace Poni.Infrastructure
{
    /// <summary>
    /// Base of the in-window dialogs (sheets shown over the dimmed window, see MainWindow).
    /// A dialog ends by calling Close(result); Cancel closes with null.
    /// </summary>
    public abstract class DialogViewModel : ObservableObject
    {
        private readonly TaskCompletionSource<object?> _completion = new TaskCompletionSource<object?>();

        protected DialogViewModel()
        {
            CancelCommand = new RelayCommand(() => Close(null));
        }

        public RelayCommand CancelCommand { get; }

        /// <summary>Width of the sheet, in pixels.</summary>
        public virtual double DialogWidth => 480;

        internal Task<object?> Completion => _completion.Task;

        protected void Close(object? result) => _completion.TrySetResult(result);
    }
}
