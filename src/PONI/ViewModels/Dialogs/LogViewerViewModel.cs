using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Poni.Core;
using Poni.Infrastructure;
using Poni.Services;

namespace Poni.ViewModels
{
    /// <summary>A log line as shown in the viewer.</summary>
    public sealed class LogEntryRow
    {
        public LogEntryRow(LogEntry entry) => Entry = entry;

        public LogEntry Entry { get; }
        public string Time => Entry.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        public LogLevel Level => Entry.Level;
        public string LevelText => LocalizationService.Get(Entry.Level switch
        {
            LogLevel.Error => "Str.Log.Error",
            LogLevel.Warn => "Str.Log.Warn",
            _ => "Str.Log.Info",
        });
        public string Message => Entry.Message;
        public string Detail => Entry.Detail;
        public bool HasDetail => Entry.Detail.Length > 0;
    }

    /// <summary>
    /// "Log" viewer: PONI's daily log, one day at a time, everything or
    /// only warnings / errors. Read-only; copy and "open folder" for sharing a bug report.
    /// </summary>
    public sealed class LogViewerViewModel : DialogViewModel
    {
        private List<(DateTime Day, string Path)> _days = new List<(DateTime, string)>();
        private int _dayIndex;
        private bool _problemsOnly;
        private List<LogEntry> _all = new List<LogEntry>();
        private IReadOnlyList<LogEntryRow> _entries = Array.Empty<LogEntryRow>();
        private bool _copied;
        private readonly DispatcherTimer _copiedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };

        public LogViewerViewModel()
        {
            OlderCommand = new RelayCommand(() => ShowDay(_dayIndex + 1), () => _dayIndex + 1 < _days.Count);
            NewerCommand = new RelayCommand(() => ShowDay(_dayIndex - 1), () => _dayIndex > 0);
            RefreshCommand = new RelayCommand(Reload);
            CopyCommand = new RelayCommand(Copy, () => _entries.Count > 0);
            OpenFolderCommand = new RelayCommand(() => AppInfo.OpenUrl(Log.DirectoryPath ?? AppData.DataDirectory));
            _copiedTimer.Tick += (_, __) => { _copiedTimer.Stop(); Copied = false; };
            Reload();
        }

        public static Task ShowAsync() => DialogService.ShowAsync(new LogViewerViewModel());

        public override double DialogWidth => 900;

        public RelayCommand OlderCommand { get; }
        public RelayCommand NewerCommand { get; }
        public RelayCommand RefreshCommand { get; }
        public RelayCommand CopyCommand { get; }
        public RelayCommand OpenFolderCommand { get; }

        public IReadOnlyList<LogEntryRow> Entries
        {
            get => _entries;
            private set
            {
                _entries = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(EmptyText));
            }
        }

        public bool ProblemsOnly
        {
            get => _problemsOnly;
            set { if (SetProperty(ref _problemsOnly, value)) { OnPropertyChanged(nameof(ShowAll)); Filter(); } }
        }

        public bool ShowAll
        {
            get => !_problemsOnly;
            set { if (value) ProblemsOnly = false; }
        }

        public bool IsEmpty => _entries.Count == 0;
        public string EmptyText => LocalizationService.Get(_all.Count > 0 && _problemsOnly ? "Str.Log.NoProblem" : "Str.Log.Empty");

        public string Subtitle => LocalizationService.Format("Str.Log.Subtitle", Log.RetentionDays);

        public string DayLabel
        {
            get
            {
                if (_days.Count == 0) return LocalizationService.Get("Str.Log.Today");
                var day = _days[_dayIndex].Day.Date;
                if (day == DateTime.Today) return LocalizationService.Get("Str.Log.Today");
                if (day == DateTime.Today.AddDays(-1)) return LocalizationService.Get("Str.Log.Yesterday");
                var text = day.ToString("dddd d MMMM yyyy", Culture);
                return char.ToUpper(text[0], Culture) + text.Substring(1);
            }
        }

        private static CultureInfo Culture => new CultureInfo(LocalizationService.IsFrench ? "fr-FR" : "en-US");

        public string CountText
        {
            get
            {
                var warnings = _all.Count(e => e.Level == LogLevel.Warn);
                var errors = _all.Count(e => e.Level == LogLevel.Error);
                return LocalizationService.Format("Str.Log.Count", _all.Count, warnings, errors);
            }
        }

        public bool Copied
        {
            get => _copied;
            private set { if (SetProperty(ref _copied, value)) OnPropertyChanged(nameof(CopyText)); }
        }

        public string CopyText => LocalizationService.Get(_copied ? "Str.Log.Copied" : "Str.Log.Copy");

        /// <summary>Re-reads the list of days and the day shown (the log grows while PONI runs).</summary>
        private void Reload()
        {
            var shown = _days.Count > 0 ? _days[_dayIndex].Day : (DateTime?)null;
            _days = LogParser.ListDays(Log.DirectoryPath);
            var index = shown == null ? 0 : _days.FindIndex(d => d.Day == shown.Value);
            ShowDay(index < 0 ? 0 : index);
        }

        private void ShowDay(int index)
        {
            _dayIndex = Math.Max(0, Math.Min(index, _days.Count - 1));
            _all = new List<LogEntry>();
            if (_days.Count > 0)
            {
                try
                {
                    _all = LogParser.Parse(LogParser.ReadShared(_days[_dayIndex].Path));
                }
                catch (Exception ex)
                {
                    _all.Add(new LogEntry { Time = DateTime.Now, Level = LogLevel.Error, Message = ex.Message });
                }
            }
            OnPropertyChanged(nameof(DayLabel));
            OnPropertyChanged(nameof(CountText));
            Filter();
        }

        private void Filter()
        {
            Entries = _all.Where(e => !_problemsOnly || e.Level != LogLevel.Info).Select(e => new LogEntryRow(e)).ToList();
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>Copies what is shown, in the log's own format (to paste in a bug report).</summary>
        private void Copy()
        {
            var text = new StringBuilder();
            foreach (var row in _entries)
            {
                var e = row.Entry;
                text.Append(e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append("  ")
                    .Append(e.Level == LogLevel.Error ? "ERROR" : e.Level == LogLevel.Warn ? "WARN " : "INFO ").Append("  ")
                    .Append(e.Message).Append(Environment.NewLine);
                if (e.Detail.Length > 0)
                    text.Append("    ").Append(e.Detail.Replace("\n", Environment.NewLine + "    ")).Append(Environment.NewLine);
            }
            try
            {
                Clipboard.SetText(text.ToString());
                Copied = true;
                _copiedTimer.Stop();
                _copiedTimer.Start();
            }
            catch (Exception ex)
            {
                Log.Warn("Log viewer: copy to clipboard failed", ex); // clipboard held by another app
            }
        }
    }
}
