using System.Threading.Tasks;
using Poni.Infrastructure;
using Poni.ViewModels;

namespace Poni.Services
{
    /// <summary>Shows an in-window dialog and waits for its result (one dialog at a time).</summary>
    public static class DialogService
    {
        internal static MainViewModel? Host { get; set; }

        public static async Task<object?> ShowAsync(DialogViewModel dialog)
        {
            if (Host == null || Host.Dialog != null) return null;
            Host.Dialog = dialog;
            try
            {
                return await dialog.Completion;
            }
            finally
            {
                Host.Dialog = null;
            }
        }

        public static async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false)
        {
            var result = await ShowAsync(new ConfirmDialogViewModel(title, message, confirmText, destructive));
            return result is bool ok && ok;
        }
    }

    public enum ToastKind { Info, Busy, Success, Error }

    /// <summary>Status notification shown at the bottom of the window.</summary>
    public static class Toast
    {
        public static void Show(ToastKind kind, string message) => DialogService.Host?.ShowToast(kind, message);

        /// <summary>Hides a "busy" toast whose work is over (success / error toasts fade by themselves).</summary>
        public static void Hide() => DialogService.Host?.HideBusyToast();
    }
}
