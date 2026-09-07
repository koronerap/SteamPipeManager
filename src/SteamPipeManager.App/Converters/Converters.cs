using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SteamPipeManager.Core.Models;

namespace SteamPipeManager.App.Converters;

/// <summary>
/// Yerleşik <see cref="BooleanToVisibilityConverter"/> parametre kabul etmiyor;
/// bu sürüm <c>Invert</c> ile ters çevrilebiliyor.
/// </summary>
public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        return flag != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is Visibility.Visible) != Invert;
}

/// <summary>Buton "meşgul değilken etkin" gibi ters bağlamalar için.</summary>
public sealed class InverseBool : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

/// <summary>Boş veya null metinleri gizler; hata/durum satırları için.</summary>
public sealed class StringToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasText = !string.IsNullOrWhiteSpace(value as string);
        return hasText != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NullToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value is not null;
        return hasValue != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class CountToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var any = value is int count && count > 0;
        return any != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Dosya yolunu görsele çevirir. <c>OnLoad</c> önbellekleme şart: varsayılan davranış
/// dosyayı açık tutar ve önbellekteki görselin sonradan güncellenmesini engellerdi.
/// </summary>
public sealed class PathToImage : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0 || !System.IO.File.Exists(path))
        {
            return null;
        }

        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            // Bozuk ya da yarım inmiş görsel: yer tutucuya düşülür.
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Doğrulama uyarılarını hata/uyarı rengine çevirir.</summary>
public sealed class SeverityToBrush : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IssueSeverity.Error
            ? System.Windows.Media.Brushes.IndianRed
            : System.Windows.Media.Brushes.Goldenrod;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Build sonucunu yerelleştirilmiş metne çevirir. Çekirdek katman dil bilmediği için
/// çeviri burada yapılıyor.
/// </summary>
public sealed class OutcomeToText : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not BuildRecord record)
        {
            return "";
        }

        var key = (record.Outcome, record.WasPreview) switch
        {
            (BuildOutcome.Succeeded, true) => "Outcome.PreviewSucceeded",
            (BuildOutcome.Succeeded, false) => "Outcome.Succeeded",
            (BuildOutcome.Failed, _) => "Outcome.Failed",
            (BuildOutcome.Cancelled, _) => "Outcome.Cancelled",
            _ => "Outcome.Running",
        };

        return Localization.AppLocalizer.Instance.Get(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Enum değerini radyo/combo seçimiyle karşılaştırır.</summary>
public sealed class EnumToBool : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null
            ? Enum.Parse(targetType, parameter.ToString()!)
            : Binding.DoNothing;
}
