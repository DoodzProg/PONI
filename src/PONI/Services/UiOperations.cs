using System;
using System.Threading.Tasks;
using Poni.Services.HyperV;
using Poni.Services.Network;

namespace Poni.Services
{
    /// <summary>
    /// Common frame of every change made to the system from the UI (host network, RJ45 port,
    /// VM): admin check, app-wide lock, busy / success / error toasts, log.
    /// </summary>
    public static class UiOperations
    {
        /// <summary>
        /// The user is heading for a change (Apply dialog, RJ45 screen, adapter menu): start the
        /// PowerShell engine (and the Hyper-V module if needed) in the background, while he chooses.
        /// Idempotent. Keeps ~80 MB off the idle footprint.
        /// </summary>
        public static void WarmUpForChanges(bool hyperV = false)
        {
            // Not admin: no change can run, don't pay for the engine.
            if (!HostNetworkService.IsElevated()) return;
            PowerShellHost.Prewarm();
            if (hyperV && HyperVContext.Instance.IsEnabled) HyperVService.Prewarm();
        }

        /// <summary>Returns the result, or null if nothing ran (not admin, or another operation running).</summary>
        public static async Task<NetworkOperationResult?> RunAsync(string busyText, Func<Task<NetworkOperationResult>> change, Action? onSuccess = null)
        {
            if (!HostNetworkService.IsElevated() && !HyperVService.IsDemo)
            {
                Toast.Show(ToastKind.Error, LocalizationService.Get("Str.Net.NeedsAdmin"));
                return null;
            }

            Toast.Show(ToastKind.Busy, busyText);
            NetworkOperationResult? result;
            try
            {
                result = await OperationGate.RunAsync(change);
            }
            catch (Exception ex)
            {
                Log.Error("Operation failed unexpectedly", ex);
                result = new NetworkOperationResult { MessageKey = "Str.Net.Failed", Args = new object[] { "", ex.Message } };
            }
            if (result == null) return null; // another operation was running

            if (result.Success)
            {
                onSuccess?.Invoke();
                Toast.Show(ToastKind.Success, LocalizationService.Format(result.MessageKey, result.Args));
            }
            else
            {
                var message = LocalizationService.Format(result.MessageKey, result.Args);
                if (result.RolledBack) message += " " + LocalizationService.Get("Str.Net.RolledBack");
                if (result.RollbackFailed) message += " " + LocalizationService.Get("Str.Net.RollbackFailed");
                Toast.Show(ToastKind.Error, message);
            }
            return result;
        }
    }
}
