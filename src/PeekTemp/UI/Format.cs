using System.Globalization;

namespace PeekTemp.UI;

internal static class Format
{
    public const string Missing = "—";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Temp(float? celsius, bool fahrenheit) =>
        celsius is { } c ? Math.Round(fahrenheit ? c * 9 / 5 + 32 : c).ToString("0", Inv) + "°" : Missing;

    public static string Percent(float? value) =>
        value is { } v ? Math.Round(Math.Clamp(v, 0, 100)).ToString("0", Inv) + "%" : Missing;

    public static string Watts(float? value) =>
        value is { } v ? Math.Round(v).ToString("0", Inv) + " Вт" : Missing;

    public static string Memory(float? usedMb, float? totalMb)
    {
        if (usedMb is not { } used) return Missing;
        var usedGb = (used / 1024).ToString("0.0", Inv).Replace('.', ',');
        if (totalMb is not { } total || total <= 0) return usedGb + " ГБ";
        var totalGb = (total / 1024).ToString(total >= 10 * 1024 ? "0" : "0.#", Inv).Replace('.', ',');
        return $"{usedGb} / {totalGb} ГБ";
    }
}
