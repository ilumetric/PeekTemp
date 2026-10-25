using System.Globalization;

namespace PeekTemp;

internal enum Language { System, Russian, English }

internal enum CpuTempIssue { NoDriver, NotElevated, SensorNotFound }

/// <summary>UI strings in Russian and English.</summary>
internal static class Strings
{
    private static volatile bool s_russian = IsSystemRussian();

    public static void Apply(Language language) =>
        s_russian = language switch
        {
            Language.Russian => true,
            Language.English => false,
            _ => IsSystemRussian(),
        };

    private static bool IsSystemRussian() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "ru" or "be" or "uk";

    private static string T(string ru, string en) => s_russian ? ru : en;

    // Menu
    public static string CpuTempUnavailableShort => T("Темп. CPU недоступна: ", "CPU temperature unavailable: ");
    public static string Details => T("Подробнее", "Details");
    public static string View => T("Вид", "View");
    public static string TwoRows => T("Две строки", "Two rows");
    public static string OneRow => T("Одна строка", "One row");
    public static string Background => T("Фон", "Background");
    public static string ColorByTemperature => T("Цвет по температуре", "Color by temperature");
    public static string Celsius => T("Градусы Цельсия (°C)", "Celsius (°C)");
    public static string Fahrenheit => T("Градусы Фаренгейта (°F)", "Fahrenheit (°F)");
    public static string Position => T("Положение", "Position");
    public static string NearTray => T("У трея (справа)", "Next to the tray (right)");
    public static string LeftEdge => T("У левого края", "At the left edge");
    public static string ResetOffset => T("Сбросить смещение", "Reset offset");
    public static string DragHint => T("Виджет можно перетащить мышью", "Drag the widget to move it");
    public static string Refresh => T("Обновление", "Refresh rate");
    public static string Every05s => T("Каждые 0,5 с", "Every 0.5 s");
    public static string Every1s => T("Каждую секунду", "Every second");
    public static string Every2s => T("Каждые 2 с", "Every 2 s");
    public static string Every5s => T("Каждые 5 с", "Every 5 s");
    public static string Gpu => T("Видеокарта", "Graphics card");
    public static string GpuAuto => T("Автоматически (дискретная)", "Automatic (discrete)");
    public static string LanguageMenu => T("Язык (Language)", "Language");
    public static string LanguageSystem => T("Как в системе", "System default");
    public static string StartWithWindows => T("Запускать при входе в Windows", "Start with Windows");
    public static string RestartAsAdmin => T("Перезапустить от имени администратора", "Restart as administrator");
    public static string InstallPawnIo => T("Установить драйвер PawnIO (для температуры CPU)…", "Install PawnIO driver (for CPU temperature)…");
    public static string OpenSettings => T("Открыть файл настроек", "Open settings file");
    public static string Exit => T("Выход", "Exit");

    public static string PawnIoInstalling => T("Установка драйвера PawnIO через winget...", "Installing the PawnIO driver with winget...");
    public static string PawnIoDone => T("Готово. Перезапустите PeekTemp от имени администратора.", "Done. Restart PeekTemp as administrator.");

    // Flyout
    public static string Processor => T("Процессор", "Processor");
    public static string GraphicsCard => T("Видеокарта", "Graphics card");
    public static string Load => T("Загрузка", "Load");
    public static string Power => T("Мощность", "Power");
    public static string Memory => T("Память", "Memory");
    public static string Temperature => T("Температура", "Temperature");
    public static string TopApps => T("Больше всего нагружают", "Most demanding apps");
    public static string Measuring => T("Подсчёт…", "Measuring…");
    public static string NoActiveApps => T("Нет заметной нагрузки", "No noticeable load");
    public static string Minutes => T("мин", "min");
    public static string Seconds => T("с", "s");
    public static string Watt => T("Вт", "W");
    public static string Gigabyte => T("ГБ", "GB");
    public static string CpuTempUnavailable => T("Температура CPU недоступна. ", "CPU temperature unavailable. ");
    public static string NoData => T("нет данных", "no data");
    public static string Source => T("Источник: ", "Source: ");
    public static string Administrator => T(" · администратор", " · administrator");
    public static string FlyoutHint => T("Правый клик — настройки. Виджет можно перетащить мышью.",
        "Right-click for settings. Drag the widget to move it.");

    public static char DecimalSeparator => s_russian ? ',' : '.';

    public static string Issue(CpuTempIssue issue) => issue switch
    {
        CpuTempIssue.NoDriver => T("Нужен драйвер PawnIO и права администратора", "The PawnIO driver and administrator rights are required"),
        CpuTempIssue.NotElevated => T("Нужны права администратора", "Administrator rights are required"),
        _ => T("Датчик температуры CPU не найден", "CPU temperature sensor not found"),
    };
}
