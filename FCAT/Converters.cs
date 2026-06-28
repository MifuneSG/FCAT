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

// true → cyan accent (for active-tab underlines), false → transparent
public class BoolToAccentBrushConverter : IValueConverter
{
    private static readonly Brush On = new SolidColorBrush(Color.FromRgb(0x4d, 0xb8, 0xd4));
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? On : Brushes.Transparent;
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

// Nav-rail active state: compares the shell's ActiveNav string to a button's key (ConverterParameter).
// Returns true when this nav item is the active one.
public class NavActiveConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// Same comparison, but yields the cyan accent brush when active and a dim brush otherwise —
// used to colour the nav icon + label for the active item.
public class NavActiveBrushConverter : IValueConverter
{
    public Brush ActiveBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x4d, 0xb8, 0xd4));
    public Brush InactiveBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x5c, 0x64, 0x73));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase)
            ? ActiveBrush : InactiveBrush;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// Multi-binding form for use in a shared Style trigger: [0]=ActiveNav, [1]=button's Tag (its key).
// Returns true when the button represents the active nav item.
public class NavActiveMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length == 2
           && string.Equals(values[0] as string, values[1] as string, StringComparison.OrdinalIgnoreCase);
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
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
