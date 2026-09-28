using Poni.Core;
using Xunit;

namespace Poni.Tests
{
    public class Ipv4Tests
    {
        [Theory]
        [InlineData("192.168.1.10", 0xC0A8010Au)]
        [InlineData("0.0.0.0", 0u)]
        [InlineData("255.255.255.255", 0xFFFFFFFFu)]
        [InlineData(" 10.0.0.1 ", 0x0A000001u)]
        public void Parses_valid_addresses(string text, uint expected)
        {
            Assert.True(Ipv4.TryParse(text, out var value));
            Assert.Equal(expected, value);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("192.168.1")]
        [InlineData("192.168.1.10.5")]
        [InlineData("192.168.1.256")]
        [InlineData("192.168.1.555")]      // invalid addresses accepted by v1
        [InlineData("999.1.1.1")]          // invalid addresses accepted by v1
        [InlineData("192.168.01.1")]       // leading zero (octal ambiguity)
        [InlineData("192.168.1.-1")]
        [InlineData("192.168.1.a")]
        [InlineData("192.168..1")]
        [InlineData("192. 168.1.1")]
        public void Rejects_invalid_addresses(string? text)
        {
            Assert.False(Ipv4.TryParse(text, out _));
        }

        [Theory]
        [InlineData("255.255.255.0", 24)]
        [InlineData("255.255.0.0", 16)]
        [InlineData("255.255.255.255", 32)]
        [InlineData("128.0.0.0", 1)]
        [InlineData("255.255.240.0", 20)]
        [InlineData("24", 24)]
        [InlineData("/24", 24)]
        [InlineData(" / 8 ", 8)]
        [InlineData("32", 32)]
        public void Parses_masks_and_prefixes(string text, int expected)
        {
            Assert.True(Ipv4.TryParseMask(text, out var prefix));
            Assert.Equal(expected, prefix);
        }

        [Theory]
        [InlineData("0.0.0.0")]            // prefix 0 is never a host configuration
        [InlineData("0")]
        [InlineData("33")]
        [InlineData("255.0.255.0")]        // not contiguous
        [InlineData("255.255.255.1")]
        [InlineData("abc")]
        [InlineData("")]
        public void Rejects_invalid_masks(string text)
        {
            Assert.False(Ipv4.TryParseMask(text, out _));
        }

        [Theory]
        [InlineData(24, "255.255.255.0")]
        [InlineData(32, "255.255.255.255")]
        [InlineData(20, "255.255.240.0")]
        [InlineData(1, "128.0.0.0")]
        public void Formats_prefix_as_mask(int prefix, string expected)
        {
            Assert.Equal(expected, Ipv4.PrefixToMaskString(prefix));
        }

        [Fact]
        public void Subnet_helpers()
        {
            Ipv4.TryParse("192.168.1.220", out var ip);
            Ipv4.TryParse("192.168.1.1", out var gw);
            Ipv4.TryParse("192.168.2.1", out var other);
            Assert.True(Ipv4.SameSubnet(ip, gw, 24));
            Assert.False(Ipv4.SameSubnet(ip, other, 24));
            Assert.True(Ipv4.SameSubnet(ip, other, 16));
            Assert.Equal("192.168.1.0", Ipv4.Format(Ipv4.NetworkAddress(ip, 24)));
            Assert.Equal("192.168.1.255", Ipv4.Format(Ipv4.BroadcastAddress(ip, 24)));
        }
    }
}
