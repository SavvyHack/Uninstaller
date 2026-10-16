using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Leftovers;

namespace Reclaim.App.Converters;

/// <summary>true → Visible. Parameter "invert" flips it.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is true;
        if (parameter is "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible ^ parameter is "invert";
}

/// <summary>Non-null / non-empty string / non-zero number / non-empty collection → Visible. "invert" flips it.</summary>
public sealed class HasValueToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i != 0,
            long l => l != 0,
            ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter is "invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value is long l ? Format.Bytes(l) : value is int i ? Format.Bytes(i) : "";

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Compares an enum/value to the parameter: used for radio-style selection and step visibility.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var eq = value is not null && parameter is not null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
        return targetType == typeof(Visibility) ? (eq ? Visibility.Visible : Visibility.Collapsed) : eq;
    }

    public object? ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null ? Enum.Parse(targetType, parameter.ToString()!) : Binding.DoNothing;
}

/// <summary>Maps a severity-like key to a WPF-UI theme brush.</summary>
public sealed class ToneToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var background = parameter is "bg";
        var key = (value?.ToString() ?? "") switch
        {
            "Certain" or "Success" or "Done" => background ? "SystemFillColorSuccessBackgroundBrush" : "SystemFillColorSuccessBrush",
            "Likely" or "Caution" or "Warning" => background ? "SystemFillColorCautionBackgroundBrush" : "SystemFillColorCautionBrush",
            "Possible" or "Neutral" => background ? "SystemFillColorNeutralBackgroundBrush" : "TextFillColorSecondaryBrush",
            "Critical" or "Failed" or "Danger" => background ? "SystemFillColorCriticalBackgroundBrush" : "SystemFillColorCriticalBrush",
            "Accent" or "Info" => background ? "SystemFillColorAttentionBackgroundBrush" : "AccentTextFillColorPrimaryBrush",
            _ => background ? "SubtleFillColorSecondaryBrush" : "TextFillColorSecondaryBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BucketTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ConfidenceBucket.Certain => "Certain: safe to remove",
        ConfidenceBucket.Likely => "Likely: review recommended",
        ConfidenceBucket.Possible => "Possible: check before removing",
        _ => value?.ToString() ?? "",
    };

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Stepper: value = current step index, parameter = this step's index → accent if reached.</summary>
public sealed class StepBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var current = value is int i ? i : 0;
        var step = int.TryParse(parameter?.ToString(), out var p) ? p : 0;
        var key = current >= step ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
