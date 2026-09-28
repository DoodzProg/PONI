using System.Linq;
using Poni.Core;
using Xunit;

namespace Poni.Tests
{
    public class ProfileValidatorTests
    {
        private static ProfileInput Input(string name = "Bureau", string ip = "192.168.1.220", string mask = "255.255.255.0",
                                          string gw = "192.168.1.1", string dns = "1.1.1.1")
            => new ProfileInput { Name = name, IPAddress = ip, Mask = mask, Gateway = gw, Dns = dns };

        private static string[] ErrorKeys(ProfileValidationResult r) => r.Errors.Select(e => e.Key).ToArray();

        [Fact]
        public void Valid_profile_is_normalized()
        {
            var r = ProfileValidator.Validate(Input(name: "  Bureau  ", dns: "1.1.1.1; 8.8.8.8, 1.1.1.1"));
            Assert.True(r.IsValid);
            Assert.Empty(r.Issues);
            Assert.Equal("Bureau", r.Profile!.Name);
            Assert.Equal(24, r.Profile.PrefixLength);
            Assert.Equal("192.168.1.1", r.Profile.Gateway);
            Assert.Equal(new[] { "1.1.1.1", "8.8.8.8" }, r.Profile.Dns); // separators mixed, duplicate removed
        }

        [Fact]
        public void Gateway_and_dns_are_optional()
        {
            var r = ProfileValidator.Validate(Input(gw: "", dns: " "));
            Assert.True(r.IsValid);
            Assert.Null(r.Profile!.Gateway);
            Assert.Empty(r.Profile.Dns);
        }

        [Fact]
        public void Mask_accepts_prefix_notation()
        {
            Assert.Equal(16, ProfileValidator.Validate(Input(mask: "/16", gw: "192.168.0.1")).Profile!.PrefixLength);
        }

        [Fact]
        public void Name_is_required_and_unique_case_insensitive()
        {
            Assert.Contains("Str.Val.NameRequired", ErrorKeys(ProfileValidator.Validate(Input(name: "   "))));
            var dup = ProfileValidator.Validate(Input(name: "bureau"), new[] { "Bureau" });
            Assert.Contains("Str.Val.NameExists", ErrorKeys(dup));
        }

        [Fact]
        public void Editing_may_keep_its_own_name()
        {
            var r = ProfileValidator.Validate(Input(name: "Bureau"), new[] { "Bureau", "Labo" }, originalName: "Bureau");
            Assert.True(r.IsValid);
        }

        [Fact]
        public void Name_length_is_limited()
        {
            var r = ProfileValidator.Validate(Input(name: new string('x', ProfileValidator.MaxNameLength + 1)));
            Assert.Contains("Str.Val.NameTooLong", ErrorKeys(r));
        }

        [Theory]
        [InlineData("1.1.1.1, 999.1.1.1", "999.1.1.1")]   // invalid addresses accepted by v1
        [InlineData("192.168.1.555", "192.168.1.555")]    // invalid addresses accepted by v1
        [InlineData("8.8.8.8 dns.google", "dns.google")]
        public void Invalid_dns_is_rejected_with_the_culprit(string dns, string culprit)
        {
            var r = ProfileValidator.Validate(Input(dns: dns));
            var issue = Assert.Single(r.Errors);
            Assert.Equal("Str.Val.DnsInvalid", issue.Key);
            Assert.Equal(ProfileField.Dns, issue.Field);
            Assert.Equal(culprit, issue.Args[0]);
        }

        [Theory]
        [InlineData("0.0.0.0")]
        [InlineData("127.0.0.1")]
        [InlineData("224.0.0.1")]
        [InlineData("255.255.255.255")]
        public void Reserved_addresses_are_rejected(string ip)
        {
            Assert.Contains("Str.Val.IpReserved", ErrorKeys(ProfileValidator.Validate(Input(ip: ip, gw: ""))));
        }

        [Fact]
        public void Network_and_broadcast_addresses_are_rejected()
        {
            Assert.Contains("Str.Val.IpIsNetwork", ErrorKeys(ProfileValidator.Validate(Input(ip: "192.168.1.0", gw: ""))));
            Assert.Contains("Str.Val.IpIsBroadcast", ErrorKeys(ProfileValidator.Validate(Input(ip: "192.168.1.255", gw: ""))));
            // /31 and /32 have no network / broadcast address: allowed.
            Assert.True(ProfileValidator.Validate(Input(ip: "10.0.0.0", mask: "31", gw: "")).IsValid);
            Assert.True(ProfileValidator.Validate(Input(ip: "10.0.0.7", mask: "32", gw: "")).IsValid);
        }

        [Fact]
        public void Apipa_is_only_a_warning()
        {
            var r = ProfileValidator.Validate(Input(ip: "169.254.10.20", mask: "16", gw: ""));
            Assert.True(r.IsValid);
            Assert.Equal("Str.Val.IpApipa", Assert.Single(r.Warnings).Key);
        }

        [Fact]
        public void Gateway_outside_subnet_is_a_warning_equal_to_ip_is_an_error()
        {
            var outside = ProfileValidator.Validate(Input(gw: "192.168.2.1"));
            Assert.True(outside.IsValid);
            Assert.Equal("Str.Val.GatewayOutsideSubnet", Assert.Single(outside.Warnings).Key);

            var same = ProfileValidator.Validate(Input(gw: "192.168.1.220"));
            Assert.Contains("Str.Val.GatewayEqualsIp", ErrorKeys(same));

            Assert.Contains("Str.Val.GatewayInvalid", ErrorKeys(ProfileValidator.Validate(Input(gw: "192.168.1"))));
        }

        [Fact]
        public void Invalid_mask_and_ip_are_both_reported()
        {
            var r = ProfileValidator.Validate(Input(ip: "300.1.1.1", mask: "255.0.255.0", gw: ""));
            var keys = ErrorKeys(r);
            Assert.Contains("Str.Val.IpInvalid", keys);
            Assert.Contains("Str.Val.MaskInvalid", keys);
            Assert.Null(r.Profile);
        }
    }
}
