using System.Windows;
using System.Windows.Threading;
using Poni.Core;
using Poni.Services;

namespace Poni
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // One PONI per data folder: two instances would each rewrite store.json and could
            // run two network changes at once. A second launch just shows the open window.
            if (!AcquireSingleInstance())
            {
                ActivateOtherInstance();
                Shutdown();
                return;
            }

            // Never die silently (v1 lesson): an unexpected UI exception is logged,
            // reported, and the app keeps running.
            DispatcherUnhandledException += OnDispatcherUnhandledException;

            // Settings + profiles (store.json; first v2 launch = migration from v1).
            AppData.Load();
            var settings = AppData.Data.Settings;

            ThemeService.Initialize(settings.Theme);
            LocalizationService.Initialize(settings.Language switch
            {
                "fr" => AppLanguage.French,
                // First launch (no choice saved yet): English, whatever the Windows language.
                // French stays one click away in Settings.
                _ => AppLanguage.English,
            });

            var window = new MainWindow();
            MainWindow = window;
            // Startup milestones in the log.
            window.ContentRendered += (_, __) => Log.Info($"Startup: first frame {StartupMs()} ms after process start");
            window.Show();
            var memoryProbe = new DispatcherTimer { Interval = System.TimeSpan.FromSeconds(8) };
            memoryProbe.Tick += (_, __) =>
            {
                memoryProbe.Stop();
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                Log.Info($"Startup: after 8 s working set {p.WorkingSet64 / (1024 * 1024)} MB, private {p.PrivateMemorySize64 / (1024 * 1024)} MB, managed {System.GC.GetTotalMemory(false) / (1024 * 1024)} MB");
            };
            memoryProbe.Start();

            // The in-process PowerShell engine (~80 MB) is NOT loaded at startup any more:
            // it is warmed up when the user heads for a change (Apply dialog, RJ45 screen, adapter
            // menu), in parallel with his choice. Reads use WMI and never need it.
            // Hyper-V module: cheap detection now, first live read (WMI, ~0.4 s) in the background.
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new System.Action(Services.HyperV.HyperVContext.Instance.Initialize));

            ReportStoreRecovery(window);
        }

        private static System.Threading.Mutex? _instanceMutex;

        private static bool AcquireSingleInstance()
        {
            // Keyed on the data folder, so PONI_DATA_DIR test instances stay independent.
            var dir = System.Environment.GetEnvironmentVariable(AppData.DataDirVariable);
            dir = string.IsNullOrWhiteSpace(dir) ? AppData.DataDirectory : dir!.Trim();
            var key = System.BitConverter.ToString(System.Security.Cryptography.SHA256.Create()
                .ComputeHash(System.Text.Encoding.UTF8.GetBytes(dir.TrimEnd('\\').ToUpperInvariant()))).Replace("-", "").Substring(0, 16);
            try
            {
                _instanceMutex = new System.Threading.Mutex(true, @"Local\PONI-" + key, out var createdNew);
                return createdNew;
            }
            catch (System.UnauthorizedAccessException)
            {
                return false; // held by an instance running with other rights: it exists
            }
        }

        private static void ActivateOtherInstance()
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            foreach (var other in System.Diagnostics.Process.GetProcessesByName(self.ProcessName))
            {
                using (other)
                {
                    if (other.Id == self.Id) continue;
                    try { NativeMethods.BringToFront(other.MainWindowHandle); } catch { }
                }
            }
        }

        private static long StartupMs()
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            return (long)(System.DateTime.Now - p.StartTime).TotalMilliseconds;
        }

        /// <summary>The store was unreadable: tell the user once, clearly (never a silent reset).</summary>
        private static void ReportStoreRecovery(Window owner)
        {
            var result = AppData.LoadResult;
            if (result == null) return;
            string? key = result.Outcome switch
            {
                StoreLoadOutcome.RecoveredFromBackup => "Str.Store.RecoveredFromBackup",
                StoreLoadOutcome.RecoveredEmpty => "Str.Store.RecoveredEmpty",
                _ => null,
            };
            if (key == null) return;
            MessageBox.Show(owner,
                string.Format(LocalizationService.Get(key), result.CorruptCopyPath ?? ""),
                "PONI", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            Log.Error("Unhandled UI exception", e.Exception);
            MessageBox.Show(
                LocalizationService.Get("Str.Error.Message") + "\n\n" + e.Exception.Message,
                LocalizationService.Get("Str.Error.Title"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
