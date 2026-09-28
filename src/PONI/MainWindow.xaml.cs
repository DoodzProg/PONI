using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Poni.Services;
using Poni.ViewModels;
using Poni.Views;

namespace Poni
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm = new MainViewModel();

        public MainWindow()
        {
            InitializeComponent();
            DataContext = _vm;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            Sidebar.Width = _vm.IsSidebarCollapsed ? SidebarFolded : SidebarUnfolded;
            Controls.Ui.SetCompact(Sidebar, _vm.IsSidebarCollapsed);
            StateChanged += (_, __) => UpdateMaximizedState();
            Loaded += (_, __) => _vm.OnStarted();
            Activated += (_, __) => _vm.OnActivated();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            NativeMethods.SetRoundCorners(this);
            NativeMethods.SetDarkFrame(this, ThemeService.IsDark);
            (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(SnapLayoutHook);
        }

        // ------------------------------------------------------------ Windows 11 snap layouts
        // Windows shows its snap layouts flyout when the pointer rests on what it believes is the
        // maximize button: our custom button is reported as HTMAXBUTTON. Windows then owns the mouse
        // over it (non-client area), so hover and click are handled here. Windows 10: no flyout,
        // the button works the same.
        private const int WM_NCHITTEST = 0x0084, WM_NCMOUSEMOVE = 0x00A0, WM_NCLBUTTONDOWN = 0x00A1,
                          WM_NCLBUTTONUP = 0x00A2, WM_NCMOUSELEAVE = 0x02A2, HTMAXBUTTON = 9;

        private IntPtr SnapLayoutHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case WM_NCHITTEST:
                    var over = IsOverMaximizeButton(lParam);
                    SetMaximizeHover(over);
                    if (!over) break;
                    handled = true;
                    return new IntPtr(HTMAXBUTTON);
                case WM_NCMOUSEMOVE:
                    if (wParam.ToInt32() != HTMAXBUTTON) SetMaximizeHover(false);
                    break;
                case WM_NCMOUSELEAVE:
                    SetMaximizeHover(false);
                    break;
                case WM_NCLBUTTONDOWN:
                    if (wParam.ToInt32() != HTMAXBUTTON) break;
                    handled = true; // no system press loop: the click is ours
                    return IntPtr.Zero;
                case WM_NCLBUTTONUP:
                    if (wParam.ToInt32() != HTMAXBUTTON) break;
                    handled = true;
                    SetMaximizeHover(false);
                    OnMaximizeRestore(this, new RoutedEventArgs());
                    return IntPtr.Zero;
            }
            return IntPtr.Zero;
        }

        private bool IsOverMaximizeButton(IntPtr lParam)
        {
            if (!MaximizeButton.IsVisible) return false;
            var raw = lParam.ToInt64();
            var screen = new Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF));
            try
            {
                var p = MaximizeButton.PointFromScreen(screen);
                return p.X >= 0 && p.Y >= 0 && p.X < MaximizeButton.ActualWidth && p.Y < MaximizeButton.ActualHeight;
            }
            catch (InvalidOperationException)
            {
                return false; // not connected to a presentation source yet
            }
        }

        private bool _maximizeHover;

        private void SetMaximizeHover(bool hover)
        {
            if (hover == _maximizeHover) return;
            _maximizeHover = hover;
            if (hover) MaximizeButton.SetResourceReference(BackgroundProperty, "CaptionHoverBrush");
            else MaximizeButton.ClearValue(BackgroundProperty);
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentPageViewModel)) AnimatePageIn();
            if (e.PropertyName == nameof(MainViewModel.IsSidebarCollapsed)) AnimateSidebar(_vm.IsSidebarCollapsed);
        }

        private const double SidebarUnfolded = 240, SidebarFolded = 72;

        /// <summary>
        /// Fold / unfold the sidebar. Folding hides the labels first, then narrows;
        /// unfolding widens first, then shows the labels (no text squeezed mid-animation).
        /// </summary>
        private void AnimateSidebar(bool fold)
        {
            var animation = new DoubleAnimation(fold ? SidebarFolded : SidebarUnfolded, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            if (fold) Controls.Ui.SetCompact(Sidebar, true);
            else animation.Completed += (_, __) => { if (!_vm.IsSidebarCollapsed) Controls.Ui.SetCompact(Sidebar, false); };
            Sidebar.BeginAnimation(WidthProperty, animation);
        }

        /// <summary>Short fade + slide-up when switching pages.</summary>
        private void AnimatePageIn()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(220);
            PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
            if (PageHost.RenderTransform is TranslateTransform shift)
                shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, duration) { EasingFunction = ease });
        }

        /// <summary>
        /// With a custom chrome, a maximized window overflows the screen by the resize border:
        /// pad the content by that amount, and swap the maximize / restore glyph.
        /// </summary>
        private void UpdateMaximizedState()
        {
            bool max = WindowState == WindowState.Maximized;
            var b = SystemParameters.WindowResizeBorderThickness;
            RootGrid.Margin = max ? new Thickness(b.Left + 4, b.Top + 4, b.Right + 4, b.Bottom + 4) : new Thickness(0);
            MaximizeButton.Tag = FindResource(max ? "Caption.Restore" : "Caption.Maximize");
            MaximizeButton.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty,
                max ? "Str.Caption.Restore" : "Str.Caption.Maximize");
        }

        /// <summary>
        /// Keyboard shortcuts: Ctrl+1/2/3 pages, Ctrl+N new profile, Ctrl+F search,
        /// F5 re-read, Enter on a focused tab / choice. Ignored while a dialog is open (Enter / Esc
        /// belong to the dialog).
        /// </summary>
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if (e.Handled || _vm.HasDialog) return;
            var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (ctrl && (key == Key.D1 || key == Key.NumPad1)) _vm.CurrentPage = AppPage.Profiles;
            else if (ctrl && (key == Key.D2 || key == Key.NumPad2) && _vm.HyperV.IsEnabled) _vm.CurrentPage = AppPage.Rj45;
            else if (ctrl && (key == Key.D3 || key == Key.NumPad3)) _vm.CurrentPage = AppPage.Settings;
            else if (ctrl && key == Key.B) Execute(_vm.ToggleSidebarCommand);
            else if (ctrl && key == Key.N)
            {
                _vm.CurrentPage = AppPage.Profiles;
                Execute(_vm.Profiles.NewProfileCommand);
            }
            else if (ctrl && key == Key.F)
            {
                _vm.CurrentPage = AppPage.Profiles;
                // The page view is (re)created by its DataTemplate: wait for it before focusing.
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => FindDescendant<ProfilesPage>(PageHost)?.FocusSearch()));
            }
            else if (key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None
                     && Keyboard.FocusedElement is System.Windows.Controls.RadioButton radio && radio.IsEnabled)
            {
                // Tab onto a sidebar entry (or a choice), Enter selects it - as Space does (user feedback).
                radio.IsChecked = true;
            }
            else if (key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (_vm.CurrentPage == AppPage.Profiles) Execute(_vm.Profiles.RefreshCommand);
                else if (_vm.CurrentPage == AppPage.Rj45) Execute(_vm.Rj45.RefreshCommand);
            }
            else return;
            e.Handled = true;
        }

        private static void Execute(ICommand command)
        {
            if (command.CanExecute(null)) command.Execute(null);
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T found) return found;
                if (FindDescendant<T>(child) is T deeper) return deeper;
            }
            return null;
        }

        private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

        private void OnMaximizeRestore(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }

        private void OnClose(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
    }
}
