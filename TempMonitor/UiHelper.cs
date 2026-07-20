using System.Windows.Media;

namespace TempMonitor;

internal static class UiHelper
{
    public static readonly System.Windows.Media.Brush NormalBrush = CreateFrozenBrush("#FFF8FBFF");
    public static readonly System.Windows.Media.Brush WarningBrush = CreateFrozenBrush("#FFA500");
    public static readonly System.Windows.Media.Brush CriticalBrush = CreateFrozenBrush("#FF4444");

    public static System.Windows.Media.Brush CreateFrozenBrush(string colorHex)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex));
        brush.Freeze();
        return brush;
    }

    public static System.Windows.Media.Brush GetAlertBrush(float value)
    {
        if (value >= 90) return CriticalBrush;
        if (value >= 80) return WarningBrush;
        return NormalBrush;
    }

    public static string FormatSpeed(float bytesPerSecond)
    {
        if (bytesPerSecond < 1024) return $"{bytesPerSecond:0.0}B";
        float kb = bytesPerSecond / 1024;
        if (kb < 1024) return $"{kb:0.0}K";
        return $"{kb / 1024.0:0.0}M";
    }

    public static string FormatOptionalTemp(float? value, string fallback = "-- °C")
    {
        return value.HasValue ? $"{value.Value:0.0} °C" : fallback;
    }

    public static string FormatOptionalGb(float? value, string fallback = "-- GB")
    {
        return value.HasValue ? $"{value.Value:F1} GB" : fallback;
    }
}
