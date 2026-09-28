using System;
using System.Collections.Generic;
using System.Linq;

namespace Poni.Core
{
    public enum ProfileField { Name, IPAddress, Mask, Gateway, Dns }

    /// <summary>One validation message. Key = a "Str.Val.*" string resource, Args = its format arguments.</summary>
    public sealed class ValidationIssue
    {
        public ValidationIssue(ProfileField field, string key, bool isWarning, params object[] args)
        {
            Field = field;
            Key = key;
            IsWarning = isWarning;
            Args = args;
        }

        public ProfileField Field { get; }
        public string Key { get; }
        public bool IsWarning { get; }
        public object[] Args { get; }

        public override string ToString() => Key + (Args.Length > 0 ? " (" + string.Join(", ", Args) + ")" : "");
    }

    /// <summary>Raw text typed in the profile form (or read from an imported file).</summary>
    public sealed class ProfileInput
    {
        public string? Name { get; set; }
        public string? IPAddress { get; set; }
        /// <summary>"255.255.255.0", "24" or "/24".</summary>
        public string? Mask { get; set; }
        public string? Gateway { get; set; }
        /// <summary>Servers separated by commas, semicolons or spaces.</summary>
        public string? Dns { get; set; }
    }

    public sealed class ProfileValidationResult
    {
        public List<ValidationIssue> Issues { get; } = new List<ValidationIssue>();
        public IEnumerable<ValidationIssue> Errors => Issues.Where(i => !i.IsWarning);
        public IEnumerable<ValidationIssue> Warnings => Issues.Where(i => i.IsWarning);
        public bool IsValid => !Errors.Any();

        /// <summary>The normalized profile (only when IsValid). Does not carry CreatedOn / LastTarget.</summary>
        public NetworkProfile? Profile { get; set; }
    }

    /// <summary>
    /// Validation of a profile. Used by the form, by import and by the v1 migration,
    /// so the rules are the same everywhere.
    /// </summary>
    public static class ProfileValidator
    {
        public const int MaxNameLength = 64;

        private static readonly char[] DnsSeparators = { ',', ';', ' ', '\t', '\r', '\n' };

        /// <param name="existingNames">Names already in use (case-insensitive).</param>
        /// <param name="originalName">When editing: the profile's current name, which may be kept.</param>
        public static ProfileValidationResult Validate(ProfileInput input, IEnumerable<string>? existingNames = null, string? originalName = null)
        {
            var result = new ProfileValidationResult();
            var issues = result.Issues;

            // Name
            var name = (input.Name ?? "").Trim();
            if (name.Length == 0)
                issues.Add(new ValidationIssue(ProfileField.Name, "Str.Val.NameRequired", false));
            else if (name.Length > MaxNameLength)
                issues.Add(new ValidationIssue(ProfileField.Name, "Str.Val.NameTooLong", false, MaxNameLength));
            else if (existingNames != null
                     && !string.Equals(name, originalName?.Trim(), StringComparison.OrdinalIgnoreCase)
                     && existingNames.Any(n => string.Equals(n?.Trim(), name, StringComparison.OrdinalIgnoreCase)))
                issues.Add(new ValidationIssue(ProfileField.Name, "Str.Val.NameExists", false, name));

            // Mask (parsed first: the IP checks below depend on the prefix)
            bool maskOk = Ipv4.TryParseMask(input.Mask, out int prefix);
            if (!maskOk)
                issues.Add(new ValidationIssue(ProfileField.Mask, "Str.Val.MaskInvalid", false));

            // IP address
            bool ipOk = Ipv4.TryParse(input.IPAddress, out uint ip);
            if (!ipOk)
            {
                issues.Add(new ValidationIssue(ProfileField.IPAddress, "Str.Val.IpInvalid", false));
            }
            else if (IsReserved(ip))
            {
                ipOk = false;
                issues.Add(new ValidationIssue(ProfileField.IPAddress, "Str.Val.IpReserved", false, Ipv4.Format(ip)));
            }
            else
            {
                if ((ip >> 16) == 0xA9FE) // 169.254.0.0/16
                    issues.Add(new ValidationIssue(ProfileField.IPAddress, "Str.Val.IpApipa", true));
                if (maskOk && prefix <= 30)
                {
                    if (ip == Ipv4.NetworkAddress(ip, prefix))
                    {
                        ipOk = false;
                        issues.Add(new ValidationIssue(ProfileField.IPAddress, "Str.Val.IpIsNetwork", false, Ipv4.Format(ip), prefix));
                    }
                    else if (ip == Ipv4.BroadcastAddress(ip, prefix))
                    {
                        ipOk = false;
                        issues.Add(new ValidationIssue(ProfileField.IPAddress, "Str.Val.IpIsBroadcast", false, Ipv4.Format(ip), prefix));
                    }
                }
            }

            // Gateway (optional)
            string? gateway = null;
            var gwText = (input.Gateway ?? "").Trim();
            if (gwText.Length > 0)
            {
                if (!Ipv4.TryParse(gwText, out uint gw) || IsReserved(gw))
                {
                    issues.Add(new ValidationIssue(ProfileField.Gateway, "Str.Val.GatewayInvalid", false));
                }
                else
                {
                    gateway = Ipv4.Format(gw);
                    if (ipOk && gw == ip)
                        issues.Add(new ValidationIssue(ProfileField.Gateway, "Str.Val.GatewayEqualsIp", false));
                    else if (ipOk && maskOk && !Ipv4.SameSubnet(ip, gw, prefix))
                        issues.Add(new ValidationIssue(ProfileField.Gateway, "Str.Val.GatewayOutsideSubnet", true, gateway, Ipv4.Format(Ipv4.NetworkAddress(ip, prefix)), prefix));
                }
            }

            // DNS (optional, any number, duplicates removed, order kept)
            var dns = new List<string>();
            foreach (var entry in (input.Dns ?? "").Split(DnsSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Ipv4.TryParse(entry, out uint server) || IsReserved(server))
                {
                    issues.Add(new ValidationIssue(ProfileField.Dns, "Str.Val.DnsInvalid", false, entry));
                    continue;
                }
                var formatted = Ipv4.Format(server);
                if (!dns.Contains(formatted)) dns.Add(formatted);
            }

            if (result.IsValid)
            {
                result.Profile = new NetworkProfile
                {
                    Name = name,
                    IPAddress = Ipv4.Format(ip),
                    PrefixLength = prefix,
                    Gateway = gateway,
                    Dns = dns,
                };
            }
            return result;
        }

        /// <summary>Validates an existing profile object (import, migration): same rules as the form.</summary>
        public static ProfileValidationResult Validate(NetworkProfile profile, IEnumerable<string>? existingNames = null)
            => Validate(ToInput(profile), existingNames);

        public static ProfileInput ToInput(NetworkProfile profile) => new ProfileInput
        {
            Name = profile.Name,
            IPAddress = profile.IPAddress,
            Mask = profile.PrefixLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Gateway = profile.Gateway,
            Dns = string.Join(", ", profile.Dns ?? new List<string>()),
        };

        /// <summary>0.0.0.0/8, loopback 127/8, multicast 224/4, class E 240/4 and broadcast are never host addresses.</summary>
        private static bool IsReserved(uint address)
        {
            uint first = address >> 24;
            return first == 0 || first == 127 || first >= 224;
        }
    }
}
