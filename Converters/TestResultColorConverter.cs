using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace YouTubeTool.Converters;

public class TestResultColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value as string ?? string.Empty;
        if (text.StartsWith("✓")) return Brushes.Green;
        if (text.StartsWith("✗")) return Brushes.Red;
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class IsShortToViewboxConverter : IValueConverter
{
    // Regular video: show full image (0,0,1,1)
    // Short: show only the middle third horizontally (1/3, 0, 1/3, 1)
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? new Rect(1.0 / 3.0, 0, 1.0 / 3.0, 1) : new Rect(0, 0, 1, 1);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class IsNotNullConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value != null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
