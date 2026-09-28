using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Poni.Core
{
    public enum LogLevel { Info, Warn, Error }

    /// <summary>One line of PONI's log, with its indented continuation (exception details).</summary>
    public sealed class LogEntry
    {
        public DateTime Time { get; set; }
        public LogLevel Level { get; set; }
        public string Message { get; set; } = "";
        public string Detail { get; set; } = "";
    }

    /// <summary>
    /// Reads the files written by Services.Log ("yyyy-MM-dd HH:mm:ss.fff  LEVEL  message", details
    /// indented by 4 spaces on the next lines). Read-only, never locks the file being written.
    /// </summary>
    public static class LogParser
    {
        private const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

        public static List<LogEntry> Parse(string text)
        {
            var entries = new List<LogEntry>();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length >= TimeFormat.Length
                    && DateTime.TryParseExact(line.Substring(0, TimeFormat.Length), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                {
                    var rest = line.Substring(TimeFormat.Length).TrimStart();
                    var space = rest.IndexOf(' ');
                    var level = space < 0 ? rest : rest.Substring(0, space);
                    entries.Add(new LogEntry
                    {
                        Time = time,
                        Level = level == "ERROR" ? LogLevel.Error : level == "WARN" ? LogLevel.Warn : LogLevel.Info,
                        Message = space < 0 ? "" : rest.Substring(space).Trim(),
                    });
                }
                else if (entries.Count > 0 && line.Trim().Length > 0)
                {
                    var last = entries[entries.Count - 1];
                    var detail = line.StartsWith("    ", StringComparison.Ordinal) ? line.Substring(4) : line;
                    last.Detail = last.Detail.Length == 0 ? detail : last.Detail + "\n" + detail;
                }
            }
            return entries;
        }

        /// <summary>The daily log files of a folder, newest first.</summary>
        public static List<(DateTime Day, string Path)> ListDays(string? directory)
        {
            var days = new List<(DateTime, string)>();
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return days;
            foreach (var file in Directory.GetFiles(directory, "poni-*.log"))
            {
                var stamp = Path.GetFileNameWithoutExtension(file).Substring("poni-".Length);
                if (DateTime.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                    days.Add((day, file));
            }
            days.Sort((a, b) => b.Item1.CompareTo(a.Item1));
            return days;
        }

        /// <summary>Reads a log file while PONI may be appending to it.</summary>
        public static string ReadShared(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }
}
