using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Poni.Controls
{
    /// <summary>Attached properties used by the design-system templates (Themes/Controls.xaml).</summary>
    public static class Ui
    {
        /// <summary>Corner radius of a templated button / surface.</summary>
        public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
            "CornerRadius", typeof(CornerRadius), typeof(Ui), new FrameworkPropertyMetadata(new CornerRadius(0)));

        public static CornerRadius GetCornerRadius(DependencyObject element) => (CornerRadius)element.GetValue(CornerRadiusProperty);
        public static void SetCornerRadius(DependencyObject element, CornerRadius value) => element.SetValue(CornerRadiusProperty, value);

        /// <summary>
        /// Inherited: true inside the folded sidebar (icons only). Templates hide their labels on it.
        /// </summary>
        public static readonly DependencyProperty CompactProperty = DependencyProperty.RegisterAttached(
            "Compact", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

        public static bool GetCompact(DependencyObject element) => (bool)element.GetValue(CompactProperty);
        public static void SetCompact(DependencyObject element, bool value) => element.SetValue(CompactProperty, value);

        /// <summary>
        /// On a Button: a left click opens the button's ContextMenu under it ("..." menus).
        /// The menu's DataContext is the button's, so its items can bind to the row / card.
        /// </summary>
        public static readonly DependencyProperty OpensMenuProperty = DependencyProperty.RegisterAttached(
            "OpensMenu", typeof(bool), typeof(Ui), new PropertyMetadata(false, OnOpensMenuChanged));

        public static bool GetOpensMenu(DependencyObject element) => (bool)element.GetValue(OpensMenuProperty);
        public static void SetOpensMenu(DependencyObject element, bool value) => element.SetValue(OpensMenuProperty, value);

        private static void OnOpensMenuChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is Button button)) return;
            button.Click -= OpenMenu;
            if (e.NewValue is true) button.Click += OpenMenu;
        }

        private static void OpenMenu(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            var menu = button.ContextMenu;
            if (menu == null) return;
            menu.DataContext = button.DataContext;
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.HorizontalOffset = -12;
            menu.IsOpen = true;
        }
    }
}
