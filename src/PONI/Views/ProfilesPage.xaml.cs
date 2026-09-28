using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Poni.ViewModels;

namespace Poni.Views
{
    public partial class ProfilesPage : UserControl
    {
        private Point _gripStart;
        private ProfileRowViewModel? _gripRow;
        private ProfileRowViewModel? _hinted;

        public ProfilesPage() => InitializeComponent();

        /// <summary>Ctrl+F (MainWindow): put the cursor in the search field.</summary>
        public void FocusSearch()
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }

        // ------------------------------------------------------------ custom order: drag and drop
        // The 6-dot handle starts the drag; the rows list shows a blue line where the profile will
        // land and moves it on drop. The order itself lives in ProfilesViewModel / ProfileOrder.

        private void OnGripDown(object sender, MouseButtonEventArgs e)
        {
            _gripRow = (sender as FrameworkElement)?.DataContext as ProfileRowViewModel;
            _gripStart = e.GetPosition(this);
            ((UIElement)sender).CaptureMouse(); // keep receiving moves even if the pointer leaves the thin handle
            e.Handled = true; // no double-click "apply" from the handle
        }

        private void OnGripUp(object sender, MouseButtonEventArgs e)
        {
            _gripRow = null;
            ((UIElement)sender).ReleaseMouseCapture();
        }

        private void OnGripMove(object sender, MouseEventArgs e)
        {
            if (_gripRow == null || e.LeftButton != MouseButtonState.Pressed) { _gripRow = null; return; }
            var delta = e.GetPosition(this) - _gripStart;
            if (System.Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance
                && System.Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance) return;

            var row = _gripRow;
            _gripRow = null;
            ((UIElement)sender).ReleaseMouseCapture();
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(ProfileRowViewModel), row), DragDropEffects.Move);
            ClearHint();
        }

        private void OnRowsDragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            if (!(e.Data.GetData(typeof(ProfileRowViewModel)) is ProfileRowViewModel moved)) return;
            var (target, below) = RowUnder(e);
            if (target == null || target == moved) { ClearHint(); return; }
            e.Effects = DragDropEffects.Move;
            if (_hinted != target) ClearHint();
            _hinted = target;
            target.DropHint = below ? DropHint.Below : DropHint.Above;
        }

        private void OnRowsDragLeave(object sender, DragEventArgs e) => ClearHint();

        private void OnRowsDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            var (target, below) = RowUnder(e);
            ClearHint();
            if (e.Data.GetData(typeof(ProfileRowViewModel)) is ProfileRowViewModel moved && target != null
                && DataContext is ProfilesViewModel vm)
                vm.Drop(moved, target, below);
        }

        /// <summary>The row under the pointer, and whether the pointer is on its lower half.</summary>
        private static (ProfileRowViewModel? Row, bool Below) RowUnder(DragEventArgs e)
        {
            var element = e.OriginalSource as DependencyObject;
            while (element != null)
            {
                if (element is ContentPresenter presenter && presenter.Content is ProfileRowViewModel row
                    && VisualTreeHelper.GetParent(presenter) is Panel) // the item container, not an inner presenter
                {
                    var y = e.GetPosition(presenter).Y;
                    return (row, y > presenter.ActualHeight / 2);
                }
                element = element is Visual || element is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(element)
                    : LogicalTreeHelper.GetParent(element);
            }
            return (null, false);
        }

        private void ClearHint()
        {
            if (_hinted != null) _hinted.DropHint = DropHint.None;
            _hinted = null;
        }
    }
}
