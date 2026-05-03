using System;
using System.Globalization;
using System.Windows.Data;

namespace RustTerminal
{
    public sealed class RunningButtonEnabledConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values is null || values.Length < 2)
            {
                return true;
            }

            var isRunInProgress = values[^1] is bool b && b;
            if (!isRunInProgress)
            {
                return true;
            }

            if (values.Length == 3)
            {
                var command = values[0]?.ToString()?.Trim() ?? string.Empty;
                var running = values[1]?.ToString()?.Trim() ?? string.Empty;
                return command.Length > 0 && string.Equals(command, running, StringComparison.Ordinal);
            }

            if (values.Length == 2 && values[0] is bool isRunningFavorite)
            {
                return isRunningFavorite;
            }

            return false;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
