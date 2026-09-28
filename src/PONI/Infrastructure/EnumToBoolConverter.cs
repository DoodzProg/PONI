using System;
using System.Globalization;
using System.Windows.Data;

namespace Poni.Infrastructure
{
    /// <summary>
    /// Binds a RadioButton's IsChecked to an enum property:
    /// IsChecked="{Binding Mode, Converter={StaticResource EnumToBool}, ConverterParameter=Dark}".
    /// </summary>
    public sealed class EnumToBoolConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value != null && parameter != null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is true && parameter != null)
                return Enum.Parse(targetType, parameter.ToString()!);
            return Binding.DoNothing;
        }
    }
}
