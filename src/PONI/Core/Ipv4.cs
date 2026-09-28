using System.Globalization;

namespace Poni.Core
{
    /// <summary>Strict IPv4 helpers (no DNS lookups, no locale dependency).</summary>
    public static class Ipv4
    {
        /// <summary>
        /// Parses a dotted-quad address. Strict: exactly 4 decimal octets 0-255, no spaces
        /// inside, no leading zeros ("010.0.0.1" is rejected: some tools read it as octal).
        /// </summary>
        public static bool TryParse(string? text, out uint value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text!.Trim().Split('.');
            if (parts.Length != 4) return false;
            foreach (var part in parts)
            {
                if (part.Length == 0 || part.Length > 3) return false;
                foreach (char ch in part)
                    if (ch < '0' || ch > '9') return false;
                if (part.Length > 1 && part[0] == '0') return false;
                int octet = int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture);
                if (octet > 255) return false;
                value = (value << 8) | (uint)octet;
            }
            return true;
        }

        public static bool IsValid(string? text) => TryParse(text, out _);

        public static string Format(uint value) =>
            string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3}",
                value >> 24, (value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);

        /// <summary>Normalizes an address ("192.168.1.10 " -> "192.168.1.10"); null if invalid.</summary>
        public static string? Normalize(string? text) => TryParse(text, out var v) ? Format(v) : null;

        public static uint PrefixToMask(int prefix) =>
            prefix <= 0 ? 0u : prefix >= 32 ? uint.MaxValue : uint.MaxValue << (32 - prefix);

        /// <summary>24 -> "255.255.255.0".</summary>
        public static string PrefixToMaskString(int prefix) => Format(PrefixToMask(prefix));

        /// <summary>
        /// Accepts a subnet mask ("255.255.255.0") or a prefix length ("24" or "/24").
        /// The mask must be contiguous; the prefix must be 1-32 (0 is never a usable host config).
        /// </summary>
        public static bool TryParseMask(string? text, out int prefix)
        {
            prefix = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var t = text!.Trim();
            if (t.StartsWith("/")) t = t.Substring(1).Trim();

            if (t.Length > 0 && t.Length <= 2 && IsDigits(t))
            {
                int n = int.Parse(t, NumberStyles.None, CultureInfo.InvariantCulture);
                if (n < 1 || n > 32) return false;
                prefix = n;
                return true;
            }

            if (!TryParse(t, out var mask)) return false;
            uint inverse = ~mask;
            if ((inverse & (inverse + 1)) != 0) return false; // ones must be contiguous from the left
            int bits = CountBits(mask);
            if (bits < 1) return false;
            prefix = bits;
            return true;
        }

        public static bool SameSubnet(uint a, uint b, int prefix)
        {
            uint mask = PrefixToMask(prefix);
            return (a & mask) == (b & mask);
        }

        public static uint NetworkAddress(uint address, int prefix) => address & PrefixToMask(prefix);

        public static uint BroadcastAddress(uint address, int prefix) => address | ~PrefixToMask(prefix);

        private static bool IsDigits(string s)
        {
            foreach (char ch in s)
                if (ch < '0' || ch > '9') return false;
            return true;
        }

        private static int CountBits(uint v)
        {
            int count = 0;
            while (v != 0) { count += (int)(v & 1); v >>= 1; }
            return count;
        }
    }
}
