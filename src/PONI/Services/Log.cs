using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Poni.Services
{
    /// <summary>
    /// Diagnostic log: %APPDATA%\PONI\logs\poni-yyyyMMdd.log, one file per day, files older than
    /// 30 days deleted at startup (v1's poni-diag.log grew forever).
    /// Logging never throws.
    /// </summary>
    public static class Log
    {
        public const int RetentionDays = 30;

        private static readonly object Sync = new object();
        private static string? _directory;

        public static string? DirectoryPath => _directory;

        public static void Initialize(string dataDirectory)
        {
            try
            {
                _directory = Path.Combine(dataDirectory, "logs");
                Directory.CreateDirectory(_directory);
                var limit = DateTime.Now.AddDays(-RetentionDays);
                foreach (var file in Directory.GetFiles(_directory, "poni-*.log"))
                {
                    if (File.GetLastWriteTime(file) < limit) File.Delete(file);
                }
            }
            catch
            {
                // No log folder: PONI works without it.
            }
        }

        public static void Info(string message) => Write("INFO ", message, null);

        public static void Warn(string message, Exception? ex = null) => Write("WARN ", message, ex);

        public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

        private static void Write(string level, string message, Exception? ex)
        {
            if (_directory == null) return;
            var now = DateTime.Now;
            var line = new StringBuilder()
                .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append("  ").Append(level).Append("  ").Append(message);
            if (ex != null) line.Append(Environment.NewLine).Append("    ").Append(ex.ToString().Replace(Environment.NewLine, Environment.NewLine + "    "));
            line.Append(Environment.NewLine);

            try
            {
                lock (Sync)
                {
                    var file = Path.Combine(_directory, "poni-" + now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
                    File.AppendAllText(file, line.ToString(), Encoding.UTF8);
                }
            }
            catch
            {
                // Never let logging break the app.
            }
        }
    }
}
