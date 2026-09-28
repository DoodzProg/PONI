using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Poni.Core;
using Xunit;

namespace Poni.Tests
{
    /// <summary>store.json on disk. Every test works in its own temp folder.</summary>
    public sealed class ProfileStoreTests : IDisposable
    {
        private readonly string _dir;

        public ProfileStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "poni-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static string Fixture(string name) => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", name);

        private static StoreData Sample(string name = "Bureau")
        {
            var data = new StoreData();
            data.Settings.Theme = ThemePreference.Light;
            data.Profiles.Add(new NetworkProfile { Name = name, IPAddress = "192.168.1.220", PrefixLength = 24 });
            return data;
        }

        private static string Hash(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return Convert.ToBase64String(sha.ComputeHash(stream));
        }

        [Fact]
        public void Empty_folder_gives_a_new_store()
        {
            var result = new ProfileStore(_dir).Load();
            Assert.Equal(StoreLoadOutcome.Created, result.Outcome);
            Assert.Empty(result.Data.Profiles);
            Assert.False(result.NeedsSave);
        }

        [Fact]
        public void Save_then_load_round_trips_without_leftovers()
        {
            var store = new ProfileStore(_dir);
            store.Save(Sample());
            store.Save(Sample("Bureau 2")); // second save goes through File.Replace

            var result = new ProfileStore(_dir).Load();
            Assert.Equal(StoreLoadOutcome.Loaded, result.Outcome);
            Assert.Equal("Bureau 2", Assert.Single(result.Data.Profiles).Name);
            Assert.Equal(ThemePreference.Light, result.Data.Settings.Theme);
            Assert.False(File.Exists(store.FilePath + ".tmp"));
            Assert.True(File.Exists(store.BackupPath)); // previous version kept
        }

        [Fact]
        public void First_launch_migrates_v1_and_leaves_it_untouched()
        {
            var v1 = Path.Combine(_dir, "profiles.json");
            File.Copy(Fixture("v1-profiles.json"), v1);
            var before = Hash(v1);

            var store = new ProfileStore(_dir, new[] { v1 });
            var result = store.Load();

            Assert.Equal(StoreLoadOutcome.MigratedFromV1, result.Outcome);
            Assert.True(result.NeedsSave);
            Assert.Equal(v1, result.MigratedFrom);
            Assert.Equal(3, result.Data.Profiles.Count);
            Assert.Single(result.Skipped);

            store.Save(result.Data);
            Assert.Equal(before, Hash(v1)); // v1 file never modified

            // Next launch reads store.json, no second migration.
            Assert.Equal(StoreLoadOutcome.Loaded, new ProfileStore(_dir, new[] { v1 }).Load().Outcome);
        }

        [Fact]
        public void First_existing_legacy_file_wins()
        {
            var missing = Path.Combine(_dir, "nope.json");
            var legacy = Path.Combine(_dir, "netmanager.json");
            File.Copy(Fixture("v1-single-profile.json"), legacy);

            var result = new ProfileStore(_dir, new[] { missing, legacy }).Load();
            Assert.Equal(legacy, result.MigratedFrom);
        }

        [Fact]
        public void Corrupted_store_is_restored_from_backup_and_kept_aside()
        {
            var store = new ProfileStore(_dir);
            store.Save(Sample("Old"));
            store.Save(Sample("New"));            // .bak now holds "Old"
            File.WriteAllText(store.FilePath, "{ \"SchemaVersion\": 2, \"Profiles\": [ oops"); // crash mid-write / bad hand edit

            var result = new ProfileStore(_dir).Load();

            Assert.Equal(StoreLoadOutcome.RecoveredFromBackup, result.Outcome);
            Assert.True(result.NeedsSave);
            Assert.Equal("Old", Assert.Single(result.Data.Profiles).Name);
            Assert.NotNull(result.CorruptCopyPath);
            Assert.True(File.Exists(result.CorruptCopyPath));
            Assert.Contains("oops", File.ReadAllText(result.CorruptCopyPath!));
        }

        [Fact]
        public void Corrupted_store_without_backup_starts_empty_but_never_crashes()
        {
            var store = new ProfileStore(_dir);
            File.WriteAllText(store.FilePath, "garbage");

            var result = store.Load();

            Assert.Equal(StoreLoadOutcome.RecoveredEmpty, result.Outcome);
            Assert.Empty(result.Data.Profiles);
            Assert.True(File.Exists(result.CorruptCopyPath));
            Assert.False(File.Exists(store.FilePath));

            store.Save(result.Data);
            Assert.Equal(StoreLoadOutcome.Loaded, new ProfileStore(_dir).Load().Outcome);
        }

        [Fact]
        public void A_v1_file_is_never_mistaken_for_a_v2_store()
        {
            // Someone copied a v1 profiles.json over store.json: no SchemaVersion -> treated as unreadable.
            var store = new ProfileStore(_dir);
            File.Copy(Fixture("v1-profiles.json"), store.FilePath);
            Assert.Equal(StoreLoadOutcome.RecoveredEmpty, store.Load().Outcome);
        }

        [Fact]
        public void Written_file_is_utf8_without_bom_and_readable()
        {
            var store = new ProfileStore(_dir);
            var data = Sample("Café – test");
            store.Save(data);
            var bytes = File.ReadAllBytes(store.FilePath);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            Assert.Equal("Café – test", new ProfileStore(_dir).Load().Data.Profiles.Single().Name);
        }
    }
}
