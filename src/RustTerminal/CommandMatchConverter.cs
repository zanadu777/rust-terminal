using System;
using System.Globalization;
using System.Windows.Data;

namespace RustTerminal
{
    public sealed class CommandMatchConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values is null || values.Length < 2)
            {
                return false;
            }

            var command = values[0]?.ToString()?.Trim() ?? string.Empty;
            var running = values[1]?.ToString()?.Trim() ?? string.Empty;
            return command.Length > 0 && string.Equals(command, running, StringComparison.Ordinal);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
