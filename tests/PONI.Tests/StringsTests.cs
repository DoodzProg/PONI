using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Poni.Tests
{
    /// <summary>
    /// Every "Str.*" key used in the code or the XAML exists in BOTH string tables, and both tables
    /// define the same keys: a missing translation would show the raw key in the UI.
    /// </summary>
    public class StringsTests
    {
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static string SourceDir()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "PONI"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "src", "PONI");
        }

        private static HashSet<string> Keys(string file) =>
            new HashSet<string>(XDocument.Load(Path.Combine(SourceDir(), "Strings", file)).Root!
                .Elements().Select(e => (string?)e.Attribute(X + "Key")).Where(k => k != null)!);

        [Fact]
        public void French_and_English_define_the_same_keys()
        {
            var fr = Keys("Strings.fr.xaml");
            var en = Keys("Strings.en.xaml");
            Assert.Empty(fr.Except(en));
            Assert.Empty(en.Except(fr));
        }

        [Fact]
        public void Every_key_used_in_the_sources_exists()
        {
            var fr = Keys("Strings.fr.xaml");
            var used = new HashSet<string>();
            var pattern = new Regex(@"""(Str\.[A-Za-z0-9_.]+)""|DynamicResource (Str\.[A-Za-z0-9_.]+)\}");
            foreach (var file in Directory.EnumerateFiles(SourceDir(), "*.*", SearchOption.AllDirectories)
                         .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains(@"\Strings\") && !f.Contains(@"\obj\") && !f.Contains(@"\bin\")))
            {
                foreach (Match m in pattern.Matches(File.ReadAllText(file)))
                    used.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            }

            // Keys built at runtime from a prefix (e.g. "Str.Net.NotConfirmed." + detail).
            var dynamicPrefixes = new[] { "Str.Net.NotConfirmed." };
            var missing = used.Where(k => !fr.Contains(k) && !dynamicPrefixes.Any(p => k == p || k == p.TrimEnd('.'))).OrderBy(k => k).ToList();
            Assert.True(missing.Count == 0, "Missing string keys: " + string.Join(", ", missing));
            foreach (var p in dynamicPrefixes)
                foreach (var suffix in new[] { "IP", "Gateway", "Dns", "Dhcp" })
                    Assert.Contains(p + suffix, fr);
        }
    }
}
