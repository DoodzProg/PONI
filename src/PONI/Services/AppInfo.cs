using System.Diagnostics;
using System.Reflection;

namespace Poni.Services
{
    /// <summary>Application identity. The version comes from PONI.csproj only (single source of truth).</summary>
    public static class AppInfo
    {
        public const string RepositoryUrl = "https://github.com/DoodzProg/PONI";
        public const string AuthorUrl = "https://doodz.dev";

        private static readonly Assembly Assembly = typeof(AppInfo).Assembly;

        /// <summary>e.g. "2.0.0-dev".</summary>
        public static string FullVersion =>
            Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetName().Version.ToString(3);

        /// <summary>e.g. "2.0" (sidebar badge).</summary>
        public static string ShortVersion => Assembly.GetName().Version.ToString(2);

        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* no default browser: nothing sensible to do */ }
        }
    }
}
