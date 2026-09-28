using System;
using System.IO;
using System.Linq;
using Poni.Core;
using Xunit;

namespace Poni.Tests
{
    /// <summary>Log viewer: reading the files written by Services.Log.</summary>
    public class LogParserTests
    {
        [Fact]
        public void Lines_levels_and_exception_details_are_read()
        {
            var text = "2026-09-28 22:11:49.168  INFO   Startup: first frame 525 ms after process start\r\n" +
                       "2026-09-28 22:11:50.001  WARN   Hyper-V WMI read failed, using the script\r\n" +
                       "    System.Management.ManagementException: Access denied\r\n" +
                       "       at Poni.Services.HyperV.WmiHyperVReader.Read()\r\n" +
                       "2026-09-28 22:11:51.500  ERROR  Operation failed unexpectedly\r\n";
            var entries = LogParser.Parse(text);

            Assert.Equal(3, entries.Count);
            Assert.Equal(new DateTime(2026, 9, 28, 22, 11, 49, 168), entries[0].Time);
            Assert.Equal(LogLevel.Info, entries[0].Level);
            Assert.Equal("Startup: first frame 525 ms after process start", entries[0].Message);
            Assert.Equal(LogLevel.Warn, entries[1].Level);
            Assert.Equal("System.Management.ManagementException: Access denied\n   at Poni.Services.HyperV.WmiHyperVReader.Read()", entries[1].Detail);
            Assert.Equal(LogLevel.Error, entries[2].Level);
            Assert.Equal("", entries[2].Detail);
        }

        [Fact]
        public void Real_log_written_by_poni_is_read_back()
        {
            var dir = Path.Combine(Path.GetTempPath(), "poni-logtest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                Poni.Services.Log.Initialize(dir);
                Poni.Services.Log.Info("hello");
                Poni.Services.Log.Error("boom", new InvalidOperationException("bad"));
                var day = Assert.Single(LogParser.ListDays(Path.Combine(dir, "logs")));
                Assert.Equal(DateTime.Today, day.Day);
                var entries = LogParser.Parse(LogParser.ReadShared(day.Path));
                Assert.Equal(new[] { "hello", "boom" }, entries.Select(e => e.Message));
                Assert.Contains("InvalidOperationException: bad", entries[1].Detail);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void Days_are_listed_newest_first_and_foreign_files_ignored()
        {
            var dir = Path.Combine(Path.GetTempPath(), "poni-logdays-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "poni-20260927.log"), "");
                File.WriteAllText(Path.Combine(dir, "poni-20260928.log"), "");
                File.WriteAllText(Path.Combine(dir, "poni-notes.log"), "");
                File.WriteAllText(Path.Combine(dir, "other.txt"), "");
                Assert.Equal(new[] { new DateTime(2026, 9, 28), new DateTime(2026, 9, 27) }, LogParser.ListDays(dir).Select(d => d.Day));
                Assert.Empty(LogParser.ListDays(Path.Combine(dir, "missing")));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
