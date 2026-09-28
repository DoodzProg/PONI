using System;
using System.IO;
using System.Linq;
using Poni.Core;
using Xunit;

namespace Poni.Tests
{
    public class StoreSerializerTests
    {
        private static string Fixture(string name) =>
            File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", name));

        [Fact]
        public void V2_round_trip_keeps_everything()
        {
            var data = new StoreData();
            data.Settings.Language = "fr";
            data.Settings.Theme = ThemePreference.Dark;
            data.Settings.ProfileSort = ProfileSort.CreatedDesc;
            data.Settings.HyperVEnabled = false;
            data.Settings.Rj45PhysicalAdapter = "Ethernet 2";
            data.Settings.SetPrivateOnApply = true;
            data.Settings.RollbackOnFailure = false;
            data.Settings.DetailedView = true;
            data.Settings.VmAllowPing = false;
            data.Settings.SidebarCollapsed = true;
            data.Profiles.Add(new NetworkProfile
            {
                Name = "Client – Site A",
                IPAddress = "10.20.30.233",
                PrefixLength = 24,
                Gateway = "10.20.30.1",
                Dns = { "8.8.8.8", "1.1.1.1" },
                CreatedOn = new DateTime(2026, 9, 28, 18, 40, 5),
                LastTarget = new LastTarget { Kind = TargetKind.VM, VM = "VM-Test", VMAdapter = "RJ45-Adapter", AppliedOn = new DateTime(2026, 9, 28, 19, 0, 0) },
            });

            var back = StoreSerializer.Deserialize(StoreSerializer.Serialize(data), out var skipped);

            Assert.Empty(skipped);
            Assert.Equal("fr", back.Settings.Language);
            Assert.Equal(ThemePreference.Dark, back.Settings.Theme);
            Assert.Equal(ProfileSort.CreatedDesc, back.Settings.ProfileSort);
            Assert.False(back.Settings.HyperVEnabled);
            Assert.Equal("Ethernet 2", back.Settings.Rj45PhysicalAdapter);
            Assert.True(back.Settings.SetPrivateOnApply);
            Assert.False(back.Settings.RollbackOnFailure);
            Assert.True(back.Settings.DetailedView);
            Assert.False(back.Settings.VmAllowPing);
            Assert.True(back.Settings.SidebarCollapsed);

            var p = Assert.Single(back.Profiles);
            Assert.Equal("Client – Site A", p.Name);
            Assert.Equal(new[] { "8.8.8.8", "1.1.1.1" }, p.Dns);
            Assert.Equal(new DateTime(2026, 9, 28, 18, 40, 5), p.CreatedOn);
            Assert.Equal(TargetKind.VM, p.LastTarget!.Kind);
            Assert.Equal("VM-Test", p.LastTarget.VM);
            Assert.Equal("RJ45-Adapter", p.LastTarget.VMAdapter);
            Assert.Equal(new DateTime(2026, 9, 28, 19, 0, 0), p.LastTarget.AppliedOn);
        }

        [Fact]
        public void V2_defaults_when_settings_are_missing_or_odd()
        {
            var data = StoreSerializer.Deserialize("{\"SchemaVersion\":2,\"Settings\":{\"Theme\":\"Purple\",\"Language\":\"de\",\"Accent\":\"Teal\",\"ProfileSort\":\"Random\"}}", out _);
            Assert.Equal(ThemePreference.System, data.Settings.Theme);
            Assert.Null(data.Settings.Language);
            Assert.True(data.Settings.RollbackOnFailure);
            Assert.False(data.Settings.SetPrivateOnApply); // no silent "Private" by default
            Assert.True(data.Settings.VmAllowPing);        // VMs answer ping by default (decision 28/09)
            Assert.False(data.Settings.SidebarCollapsed);   // sidebar unfolded by default
            Assert.Null(data.Settings.Language);            // not chosen yet -> English at startup
            Assert.Equal(ProfileSort.Custom, data.Settings.ProfileSort); // unknown value -> the stored order
            // The old "Accent" key (a selectable colour in early v2 builds) is simply ignored, and not written back.
            Assert.DoesNotContain("Accent", StoreSerializer.Serialize(data));
            Assert.Empty(data.Profiles);
        }

        [Fact]
        public void V2_requires_schema_version()
        {
            Assert.Throws<FormatException>(() => StoreSerializer.Deserialize("{\"Profiles\":[]}", out _));
            Assert.Throws<FormatException>(() => StoreSerializer.Deserialize("[]", out _));
        }

        [Fact]
        public void Migrates_a_real_v1_store()
        {
            // Fixture written by Windows PowerShell 5.1 ConvertTo-Json, exactly like PONI 1.0 (UTF-8 BOM).
            var data = StoreSerializer.FromV1(Fixture("v1-profiles.json"), out var skipped);

            Assert.Equal("fr", data.Settings.Language);
            Assert.Equal("Ethernet", data.Settings.Rj45PhysicalAdapter);
            Assert.Equal(new[] { "Client-SiteA", "Labo-VM", "Bureau" }, data.Profiles.Select(p => p.Name));

            // v1 accepted invalid DNS before its fix: that profile is reported, not silently altered.
            var bad = Assert.Single(skipped);
            Assert.Equal("T-DNS", bad.Name);
            Assert.Equal("Str.Val.DnsInvalid", bad.Reason);

            var siteA = data.Profiles[0];
            Assert.Equal("10.20.30.1", siteA.Gateway);
            Assert.Equal(new[] { "8.8.8.8", "1.1.1.1" }, siteA.Dns);
            Assert.Equal(new DateTime(2026, 8, 3, 20, 0, 0), siteA.CreatedOn);
            Assert.Equal(TargetKind.Host, siteA.LastTarget!.Kind); // French sentinel "Hote"
            Assert.Equal("Ethernet", siteA.LastTarget.Adapter);

            var labo = data.Profiles[1];
            Assert.Null(labo.Gateway);
            Assert.Empty(labo.Dns);
            Assert.Equal(TargetKind.VM, labo.LastTarget!.Kind);
            Assert.Equal("VM-Test", labo.LastTarget.VM);
            Assert.Equal("Carte-RJ45", labo.LastTarget.VMAdapter);
            Assert.Equal(new DateTime(2026, 9, 3, 17, 7, 21), labo.LastTarget.AppliedOn);
        }

        [Fact]
        public void Migrates_a_v1_store_with_a_single_unwrapped_profile()
        {
            // PowerShell turns a one-item array into a plain object: must still be read.
            var data = StoreSerializer.FromV1(Fixture("v1-single-profile.json"), out var skipped);
            Assert.Empty(skipped);
            Assert.Equal("en", data.Settings.Language);
            Assert.Null(data.Settings.Rj45PhysicalAdapter);
            var p = Assert.Single(data.Profiles);
            Assert.Equal("Bureau", p.Name);
            Assert.Equal("Wi-Fi", p.LastTarget!.Adapter);
        }
    }
}
