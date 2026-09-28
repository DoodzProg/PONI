using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Poni.Core
{
    public enum StoreLoadOutcome
    {
        /// <summary>store.json read normally.</summary>
        Loaded,
        /// <summary>Nothing found: a new empty store.</summary>
        Created,
        /// <summary>No v2 store yet: profiles imported from a v1 profiles.json (left untouched).</summary>
        MigratedFromV1,
        /// <summary>store.json was unreadable: restored from store.json.bak.</summary>
        RecoveredFromBackup,
        /// <summary>store.json and its backup were unreadable: new empty store (the bad file is kept aside).</summary>
        RecoveredEmpty,
    }

    public sealed class StoreLoadResult
    {
        public StoreLoadResult(StoreData data, StoreLoadOutcome outcome)
        {
            Data = data;
            Outcome = outcome;
        }

        public StoreData Data { get; }
        public StoreLoadOutcome Outcome { get; }
        /// <summary>v1 file the profiles came from (MigratedFromV1).</summary>
        public string? MigratedFrom { get; set; }
        /// <summary>Where the unreadable store was moved (Recovered*).</summary>
        public string? CorruptCopyPath { get; set; }
        /// <summary>Entries that could not be read (invalid profiles).</summary>
        public List<SkippedEntry> Skipped { get; set; } = new List<SkippedEntry>();
        /// <summary>True when the caller should save right away (migration, recovery).</summary>
        public bool NeedsSave => Outcome != StoreLoadOutcome.Loaded && Outcome != StoreLoadOutcome.Created;
    }

    /// <summary>
    /// Reads and writes %APPDATA%\PONI\store.json:
    /// - atomic write: temporary file, flushed to disk, then swapped in with File.Replace,
    ///   which keeps the previous version as store.json.bak;
    /// - an unreadable store never prevents PONI from starting: it is moved aside
    ///   (store.corrupt-yyyyMMdd-HHmmss.json) and the backup is used, or a new store is created;
    /// - first launch of v2: profiles are copied from the v1 profiles.json, which is left
    ///   untouched (v1 keeps working, and it doubles as a backup).
    /// </summary>
    public sealed class ProfileStore
    {
        public const string FileName = "store.json";

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private readonly IReadOnlyList<string> _legacyFiles;

        /// <param name="directory">Data folder (normally %APPDATA%\PONI).</param>
        /// <param name="legacyV1Files">v1 stores to migrate from, in order of preference.</param>
        public ProfileStore(string directory, IEnumerable<string>? legacyV1Files = null)
        {
            DirectoryPath = directory;
            _legacyFiles = (legacyV1Files ?? Enumerable.Empty<string>()).ToList();
        }

        public string DirectoryPath { get; }
        public string FilePath => Path.Combine(DirectoryPath, FileName);
        public string BackupPath => FilePath + ".bak";
        private string TempPath => FilePath + ".tmp";

        public StoreLoadResult Load()
        {
            if (File.Exists(FilePath))
            {
                try
                {
                    var data = StoreSerializer.Deserialize(File.ReadAllText(FilePath), out var skipped);
                    return new StoreLoadResult(data, StoreLoadOutcome.Loaded) { Skipped = skipped };
                }
                catch (Exception ex) when (ex is FormatException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    return Recover();
                }
            }

            foreach (var legacy in _legacyFiles)
            {
                if (!File.Exists(legacy)) continue;
                try
                {
                    var data = StoreSerializer.FromV1(File.ReadAllText(legacy), out var skipped);
                    return new StoreLoadResult(data, StoreLoadOutcome.MigratedFromV1) { MigratedFrom = legacy, Skipped = skipped };
                }
                catch (Exception ex) when (ex is FormatException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    // An unreadable v1 file is simply ignored (it stays where it is).
                }
            }

            return new StoreLoadResult(new StoreData(), StoreLoadOutcome.Created);
        }

        public void Save(StoreData data)
        {
            Directory.CreateDirectory(DirectoryPath);
            var bytes = Utf8NoBom.GetBytes(StoreSerializer.Serialize(data));

            using (var stream = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true); // really on disk before the swap
            }

            if (File.Exists(FilePath))
                File.Replace(TempPath, FilePath, BackupPath, ignoreMetadataErrors: true);
            else
                File.Move(TempPath, FilePath);
        }

        private StoreLoadResult Recover()
        {
            string corruptCopy = Path.Combine(DirectoryPath,
                "store.corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json");
            try { File.Move(FilePath, corruptCopy); }
            catch (IOException) { corruptCopy = FilePath; }

            if (File.Exists(BackupPath))
            {
                try
                {
                    var data = StoreSerializer.Deserialize(File.ReadAllText(BackupPath), out var skipped);
                    return new StoreLoadResult(data, StoreLoadOutcome.RecoveredFromBackup) { CorruptCopyPath = corruptCopy, Skipped = skipped };
                }
                catch (Exception ex) when (ex is FormatException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Backup unreadable as well: fall through to an empty store.
                }
            }
            return new StoreLoadResult(new StoreData(), StoreLoadOutcome.RecoveredEmpty) { CorruptCopyPath = corruptCopy };
        }
    }
}
