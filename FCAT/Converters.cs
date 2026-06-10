using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FCAT;

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

public class BoolToInvisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}

public class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}

public class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class CountToInvisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int count && count > 0 ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrEmpty(s) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// Maps EVE role strings to a display colour brush
public class RoleToColorConverter : IValueConverter
{
    private static readonly SolidColorBrush Gold   = new(Color.FromRgb(0xC8, 0xA9, 0x51));
    private static readonly SolidColorBrush Cyan   = new(Color.FromRgb(0x4C, 0x9B, 0xE8));
    private static readonly SolidColorBrush Orange = new(Color.FromRgb(0xE3, 0x8A, 0x20));
    private static readonly SolidColorBrush Dim    = new(Color.FromRgb(0x6E, 0x7F, 0x9A));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string role ? role switch
        {
            "fleet_commander" => Gold,
            "wing_commander"  => Cyan,
            "squad_commander" => Orange,
            _                 => Dim
        } : Dim;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// Loads network images async; caches per URL so portraits don't re-download every poll tick
public class UrlToImageConverter : IValueConverter
{
    private static readonly Dictionary<string, BitmapImage> Cache = [];

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrEmpty(url)) return null;

        if (Cache.TryGetValue(url, out var cached)) return cached;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource       = new Uri(url, UriKind.Absolute);
            bmp.DecodePixelWidth = 64;
            bmp.CreateOptions   = BitmapCreateOptions.IgnoreImageCache; // let our own cache manage it
            bmp.CacheOption     = BitmapCacheOption.OnDemand;           // async download
            bmp.EndInit();

            Cache[url] = bmp;
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
