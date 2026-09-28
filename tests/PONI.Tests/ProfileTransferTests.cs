using System;
using System.Linq;
using Poni.Core;
using Xunit;

namespace Poni.Tests
{
    /// <summary>Export / import.</summary>
    public class ProfileTransferTests
    {
        private static NetworkProfile Profile(string name, string ip = "192.168.1.10") => new NetworkProfile
        {
            Name = name,
            IPAddress = ip,
            PrefixLength = 24,
            Gateway = "192.168.1.1",
            Dns = { "1.1.1.1" },
            CreatedOn = new DateTime(2026, 9, 1),
            LastTarget = new LastTarget { Kind = TargetKind.VM, VM = "VM-Customer-Lab", VMAdapter = "RJ45-Adapter", AppliedOn = DateTime.Now },
        };

        [Fact]
        public void Export_contains_only_portable_fields()
        {
            var json = StoreSerializer.Export(new[] { Profile("Bureau") });

            Assert.Contains("\"PONIProfiles\"", json);
            Assert.Contains("\"192.168.1.10\"", json);
            // Nothing about this PC may leak into a file sent to someone else.
            Assert.DoesNotContain("LastTarget", json);
            Assert.DoesNotContain("VM-Customer-Lab", json);
            Assert.DoesNotContain("RJ45-Adapter", json);
            Assert.DoesNotContain("CreatedOn", json);
        }

        [Fact]
        public void Export_then_import_round_trips()
        {
            var json = StoreSerializer.Export(new[] { Profile("A", "10.0.0.1"), Profile("B", "10.0.0.2") });
            var read = StoreSerializer.Import(json);
            Assert.Empty(read.Skipped);
            Assert.Equal(new[] { "A", "B" }, read.Profiles.Select(p => p.Name));
            Assert.All(read.Profiles, p => Assert.Null(p.LastTarget));
        }

        [Fact]
        public void Import_skips_bad_entries_one_by_one()
        {
            // v1 bug: one non-numeric PrefixLength made the WHOLE import fail.
            const string json = @"{ ""PONIProfiles"": [
                { ""Name"": ""Good"", ""IPAddress"": ""10.0.0.5"", ""PrefixLength"": 24 },
                { ""Name"": ""BadPrefix"", ""IPAddress"": ""10.0.0.6"", ""PrefixLength"": ""abc"" },
                { ""Name"": ""BadIp"", ""IPAddress"": ""10.0.0.300"", ""PrefixLength"": 24 },
                { ""Name"": ""BadDns"", ""IPAddress"": ""10.0.0.7"", ""PrefixLength"": 24, ""DNS"": [""1.1.1.1"", ""nope""] },
                { ""IPAddress"": ""10.0.0.8"", ""PrefixLength"": 24 },
                ""not an object"",
                { ""Name"": ""good"", ""IPAddress"": ""10.0.0.9"", ""PrefixLength"": 24 },
                { ""Name"": ""AlsoGood"", ""IPAddress"": ""10.0.0.10"", ""PrefixLength"": ""16"", ""Gateway"": ""10.0.0.1"", ""DNS"": ""8.8.8.8, 1.1.1.1"" }
            ] }";

            var read = StoreSerializer.Import(json);

            Assert.Equal(new[] { "Good", "AlsoGood" }, read.Profiles.Select(p => p.Name));
            Assert.Equal(16, read.Profiles[1].PrefixLength);
            Assert.Equal(new[] { "8.8.8.8", "1.1.1.1" }, read.Profiles[1].Dns);

            var reasons = read.Skipped.Select(s => s.Name + ":" + s.Reason).ToList();
            Assert.Contains("BadPrefix:Str.Val.MaskInvalid", reasons);
            Assert.Contains("BadIp:Str.Val.IpInvalid", reasons);
            Assert.Contains("BadDns:Str.Val.DnsInvalid", reasons);
            Assert.Contains(":Str.Val.NameRequired", reasons);
            Assert.Contains(":Str.Val.NotAnObject", reasons);
            Assert.Contains("good:Str.Val.NameExists", reasons); // duplicate inside the file
        }

        [Theory]
        [InlineData(@"{ ""PONIProfiles"": [ { ""Name"": ""X"", ""IPAddress"": ""10.0.0.5"", ""PrefixLength"": 24 } ] }")]
        [InlineData(@"{ ""Profiles"": [ { ""Name"": ""X"", ""IPAddress"": ""10.0.0.5"", ""PrefixLength"": 24 } ] }")]
        [InlineData(@"[ { ""Name"": ""X"", ""IPAddress"": ""10.0.0.5"", ""PrefixLength"": 24 } ]")]
        public void Import_accepts_v1_exports_whole_stores_and_bare_arrays(string json)
        {
            Assert.Equal("X", Assert.Single(StoreSerializer.Import(json).Profiles).Name);
        }

        [Theory]
        [InlineData("not json")]
        [InlineData(@"{ ""Something"": 1 }")]
        [InlineData(@"""a string""")]
        public void Import_rejects_files_without_profiles(string json)
        {
            Assert.Throws<FormatException>(() => StoreSerializer.Import(json));
        }

        [Fact]
        public void Merge_never_overwrites_an_existing_name()
        {
            var data = new StoreData();
            data.Profiles.Add(Profile("Bureau", "192.168.1.10"));

            var result = ProfileMerge.AddImported(data, new[] { Profile("BUREAU", "10.9.9.9"), Profile("Labo", "10.0.0.2") });

            Assert.Equal(1, result.Added);
            Assert.Equal("BUREAU", Assert.Single(result.Skipped).Name);
            Assert.Equal("192.168.1.10", data.Profiles.Single(p => p.Name == "Bureau").IPAddress);
            Assert.Null(data.Profiles.Single(p => p.Name == "Labo").LastTarget);
        }
    }
}
