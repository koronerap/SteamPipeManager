using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SteamPipeManager.Core.Models;
using SteamPipeManager.Presentation.Localization;
using SteamPipeManager.Presentation.ViewModels;

namespace SteamPipeManager.Desktop.Converters;

/// <summary>Durum tonunu fırçaya çevirir; WPF sürümüyle aynı renkler.</summary>
public sealed class ToneToBrush : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        StatusTone.Success => Brushes.MediumSeaGreen,
        StatusTone.Warning => Brushes.Goldenrod,
        StatusTone.Danger => Brushes.IndianRed,
        StatusTone.Info => Brushes.CornflowerBlue,
        StatusTone.Neutral => Brushes.Gainsboro,
        _ => Brushes.Gray,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Hub'daki mağaza etiketi: Steam lacivert, Epic koyu gri.</summary>
public sealed class ProviderToBrush : IValueConverter
{
    private static readonly IBrush Steam = new SolidColorBrush(Color.FromArgb(0x55, 0x1B, 0x2A, 0x47));
    private static readonly IBrush Epic = new SolidColorBrush(Color.FromArgb(0x55, 0x2B, 0x2B, 0x2B));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is PublishProviderId.Epic ? Epic : Steam;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Diskteki görseli yükler; yoksa ya da bozuksa null (yer tutucu görünür).</summary>
public sealed class PathToBitmap : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0 || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
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

public sealed class SeverityToBrush : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IssueSeverity.Error ? Brushes.IndianRed : Brushes.Goldenrod;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Build geçmişindeki sonuç metni.</summary>
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

        return AppLocalizer.Instance.Get(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Sayı sıfırdan büyükse true; boş liste mesajları için.</summary>
public sealed class CountToBool : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is int count && count > 0) != Invert;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// ViewModel'in verdiği bir dil anahtarını metne çevirir (ör. secret'ın nerede
/// saklandığı platforma göre değişen açıklama).
/// </summary>
public sealed class LocalizeKey : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key ? AppLocalizer.Instance.Get(key) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
