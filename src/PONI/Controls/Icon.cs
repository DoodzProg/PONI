using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Poni.Controls
{
    /// <summary>
    /// A stroke icon drawn on a fixed square grid (24x24 by default) and scaled to the control
    /// size, so every icon keeps the proportions of the mockups. Colour = Foreground.
    /// Template: Themes/Controls.xaml.
    /// </summary>
    public class Icon : Control
    {
        static Icon()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(typeof(Icon)));
            FocusableProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(false));
            IsTabStopProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(false));
        }

        public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
            nameof(Data), typeof(Geometry), typeof(Icon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
            nameof(StrokeThickness), typeof(double), typeof(Icon), new FrameworkPropertyMetadata(1.8, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty GridSizeProperty = DependencyProperty.Register(
            nameof(GridSize), typeof(double), typeof(Icon), new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public Geometry? Data
        {
            get => (Geometry?)GetValue(DataProperty);
            set => SetValue(DataProperty, value);
        }

        /// <summary>Stroke width expressed on the icon grid (1.8 on a 24 grid, like the mockups).</summary>
        public double StrokeThickness
        {
            get => (double)GetValue(StrokeThicknessProperty);
            set => SetValue(StrokeThicknessProperty, value);
        }

        /// <summary>Size of the drawing grid (24 for app icons, 10 for caption buttons).</summary>
        public double GridSize
        {
            get => (double)GetValue(GridSizeProperty);
            set => SetValue(GridSizeProperty, value);
        }
    }
}
