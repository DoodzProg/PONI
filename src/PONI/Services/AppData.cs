using System;
using System.IO;
using System.Linq;
using Poni.Core;

namespace Poni.Services
{
    /// <summary>
    /// The application's persisted data (settings + profiles), loaded once at startup.
    /// Data folder: %APPDATA%\PONI, or the folder named by the PONI_DATA_DIR environment
    /// variable (tests and screenshots: never touch the real user data).
    /// </summary>
    public static class AppData
    {
        public const string DataDirVariable = "PONI_DATA_DIR";

        private static ProfileStore? _store;

        public static StoreData Data { get; private set; } = new StoreData();

        public static StoreLoadResult? LoadResult { get; private set; }

        public static string DataDirectory { get; private set; } = DefaultDirectory();

        /// <summary>True when PONI_DATA_DIR redirects the data folder (development / tests).</summary>
        public static bool IsRedirected { get; private set; }

        public static void Load()
        {
            var overrideDir = Environment.GetEnvironmentVariable(DataDirVariable);
            IsRedirected = !string.IsNullOrWhiteSpace(overrideDir);
            DataDirectory = IsRedirected ? overrideDir!.Trim() : DefaultDirectory();

            Log.Initialize(DataDirectory);
            Log.Info("PONI " + AppInfo.FullVersion + " starting, data folder: " + DataDirectory + (IsRedirected ? " (PONI_DATA_DIR)" : ""));

            // v1 stores, in order: PONI 1.0 (same folder), then the older NetManager name.
            // With PONI_DATA_DIR set, the real %APPDATA% is never read.
            var legacy = IsRedirected
                ? new[] { Path.Combine(DataDirectory, "profiles.json") }
                : new[]
                {
                    Path.Combine(DataDirectory, "profiles.json"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetManager", "profiles.json"),
                };

            _store = new ProfileStore(DataDirectory, legacy);
            try
            {
                LoadResult = _store.Load();
            }
            catch (Exception ex)
            {
                Log.Error("Store load failed, starting with an empty store.", ex);
                LoadResult = new StoreLoadResult(new StoreData(), StoreLoadOutcome.RecoveredEmpty);
            }
            Data = LoadResult.Data;

            Log.Info($"Store {LoadResult.Outcome}: {Data.Profiles.Count} profile(s)"
                     + (LoadResult.MigratedFrom != null ? ", migrated from " + LoadResult.MigratedFrom : "")
                     + (LoadResult.CorruptCopyPath != null ? ", unreadable store moved to " + LoadResult.CorruptCopyPath : ""));
            foreach (var skipped in LoadResult.Skipped)
                Log.Warn("Profile not loaded: " + skipped);

            if (LoadResult.NeedsSave) Save();
        }

        /// <summary>Persists the current data. Returns false (and logs) on failure.</summary>
        public static bool Save()
        {
            if (_store == null) return false;
            try
            {
                _store.Save(Data);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Could not save " + _store.FilePath, ex);
                return false;
            }
        }

        public static bool NameExists(string name) =>
            Data.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        private static string DefaultDirectory() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PONI");
    }
}
