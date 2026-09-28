using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Poni.Core;

namespace Poni.Services
{
    /// <summary>
    /// Swaps the colour palette (Themes/Colors.Light.xaml or Colors.Dark.xaml) and sets the accent
    /// brushes at runtime. Every colour in the UI is a DynamicResource, so a swap repaints
    /// the whole app instantly.
    /// </summary>
    public static class ThemeService
    {
        private static ResourceDictionary? _palette;

        public static ThemePreference Mode { get; private set; } = ThemePreference.System;

        public static bool IsDark { get; private set; }

        public static event EventHandler? ThemeChanged;

        public static void Initialize(ThemePreference mode)
        {
            Mode = mode;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            Apply();
        }

        public static void SetMode(ThemePreference mode)
        {
            if (mode == Mode) return;
            Mode = mode;
            Apply();
        }

        /// <summary>The ONE accent colour: Apple blue (white text at 4.5:1 or better). The accent
        /// used to be selectable (blue / teal / orange): removed, one colour, one art direction.</summary>
        public static readonly Color AccentColor = Color.FromRgb(0x00, 0x71, 0xE3);

        private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (Mode != ThemePreference.System || e.Category != UserPreferenceCategory.General) return;
            Application.Current?.Dispatcher.BeginInvoke(new Action(Apply));
        }

        private static void Apply()
        {
            IsDark = Mode == ThemePreference.Dark || (Mode == ThemePreference.System && SystemPrefersDark());

            var palette = new ResourceDictionary
            {
                Source = new Uri($"/PONI;component/Themes/Colors.{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Relative)
            };
            var merged = Application.Current.Resources.MergedDictionaries;
            if (_palette != null) merged.Remove(_palette);
            merged.Insert(0, palette);
            _palette = palette;

            ApplyAccent();
            foreach (Window window in Application.Current.Windows) NativeMethods.SetDarkFrame(window, IsDark);
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        private static void ApplyAccent()
        {
            var c = AccentColor;
            var res = Application.Current.Resources;
            res["AccentBrush"] = Frozen(new SolidColorBrush(c));
            res["AccentTintBrush"] = Frozen(new SolidColorBrush(Color.FromArgb((byte)(IsDark ? 0x33 : 0x1A), c.R, c.G, c.B)));
            res["AccentShadowColor"] = Color.FromArgb(0x4D, c.R, c.G, c.B);
        }

        private static SolidColorBrush Frozen(SolidColorBrush brush)
        {
            brush.Freeze();
            return brush;
        }

        /// <summary>Windows "app mode" setting (Settings > Personalization > Colors).</summary>
        private static bool SystemPrefersDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
