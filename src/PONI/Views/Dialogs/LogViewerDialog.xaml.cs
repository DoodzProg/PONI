using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Poni.ViewModels;

namespace Poni.Views.Dialogs
{
    public partial class LogViewerDialog : UserControl
    {
        public LogViewerDialog()
        {
            InitializeComponent();
            Loaded += (_, __) => ScrollToLatest();
            DataContextChanged += (_, e) =>
            {
                if (e.OldValue is INotifyPropertyChanged old) old.PropertyChanged -= OnViewModelChanged;
                if (e.NewValue is INotifyPropertyChanged now) now.PropertyChanged += OnViewModelChanged;
            };
        }

        private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LogViewerViewModel.Entries)) ScrollToLatest();
        }

        /// <summary>The latest lines are the interesting ones: show the end of the day.</summary>
        private void ScrollToLatest()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new System.Action(() =>
            {
                if (EntryList.Items.Count > 0) EntryList.ScrollIntoView(EntryList.Items[EntryList.Items.Count - 1]);
            }));
        }
    }
}
