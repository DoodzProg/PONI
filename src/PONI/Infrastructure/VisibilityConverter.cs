using System;
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Poni.Infrastructure
{
    /// <summary>
    /// Visible when the value is "truthy" (true, non-null, non-empty string, non-zero, non-empty
    /// collection); ConverterParameter=invert flips it.
    /// </summary>
    public sealed class VisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool visible = value switch
            {
                null => false,
                bool b => b,
                string s => s.Length > 0,
                int i => i != 0,
                ICollection c => c.Count > 0,
                _ => true,
            };
            if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) visible = !visible;
            return visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
