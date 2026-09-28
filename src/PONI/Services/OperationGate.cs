using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Poni.Services
{
    /// <summary>
    /// One system-changing operation at a time, app-wide (v1 let the user start an
    /// apply while another one was running). While busy, every command that changes the network
    /// is disabled.
    /// </summary>
    public static class OperationGate
    {
        public static bool IsBusy { get; private set; }

        public static event EventHandler? BusyChanged;

        /// <summary>Runs the operation if nothing else is running; returns default(T) if busy.</summary>
        public static async Task<T?> RunAsync<T>(Func<Task<T>> operation) where T : class
        {
            if (IsBusy) return null;
            SetBusy(true);
            try
            {
                return await operation();
            }
            finally
            {
                SetBusy(false);
            }
        }

        private static void SetBusy(bool busy)
        {
            IsBusy = busy;
            BusyChanged?.Invoke(null, EventArgs.Empty);
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
