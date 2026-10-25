using System.Net;

namespace Zephyron.Core;

/// <summary>
/// Внешний адрес самого человека — тот, под которым его видит интернет мимо туннеля.
/// </summary>
/// <remarks>
/// <para>
/// Владелец, 25.10: настоящий адрес не должен попадать в журналы и отчёты —
/// их пересылают в поддержку и выкладывают в обсуждения. Узнаётся адрес только
/// нашими же замерами (скорость напрямую, проверка выхода мимо туннеля); что
/// узнано, то и прячется во всём, что пишется в файл, звёздочками.
/// </para>
/// <para>
/// Адреса серверов VPN и выходов туннеля не прячутся: это не адрес человека,
/// а без них журнал не разобрать.
/// </para>
/// </remarks>
public static class OwnAddress
{
    /// <summary>Чем адрес заменяется в журналах и отчётах.</summary>
    public const string Hidden = "****";

    private static volatile string? _known;

    /// <summary>Узнанный адрес или <c>null</c>.</summary>
    public static string? Known => _known;

    /// <summary>Запомнить свой адрес; не адрес — ничего не меняется.</summary>
    public static void Learn(string? address)
    {
        var text = address?.Trim();

        if (!string.IsNullOrEmpty(text) && IPAddress.TryParse(text, out _))
            _known = text;
    }

    /// <summary>Текст для файла: свой адрес заменён на <see cref="Hidden"/>.</summary>
    public static string Mask(string text) =>
        _known is { } own && text.Contains(own, StringComparison.OrdinalIgnoreCase)
            ? text.Replace(own, Hidden, StringComparison.OrdinalIgnoreCase)
            : text;

    /// <summary>Для тестов: забыть адрес.</summary>
    internal static void Forget() => _known = null;
}
