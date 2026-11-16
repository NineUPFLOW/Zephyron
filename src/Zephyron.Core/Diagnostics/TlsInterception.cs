using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Zephyron.Core.Diagnostics;

/// <summary>Кто вскрывает защищённые соединения: чей сертификат пришёл вместо Google и что выключить.</summary>
/// <param name="Issuer">Кем подписан сертификат, пришедший от «Google».</param>
/// <param name="Product">Программа, если узнана по подписи; иначе <c>null</c>.</param>
/// <param name="Advice">Где это выключается — словами из самой программы.</param>
public sealed record Interception(string Issuer, string? Product, string Advice);

/// <summary>
/// Перехват HTTPS на этом компьютере: антивирус или фильтр подменяет сертификаты сайтов своими.
/// </summary>
/// <remarks>
/// <para>
/// 16.11: у пользователя Kaspersky вскрывал соединения — туннель отвергал его сертификат
/// (4674 ошибки «signed by unknown authority» на DNS), а все соединения шли по TLS 1.2,
/// которое Kaspersky выставляет от себя. Обход видит уже не браузер, а антивирус.
/// </para>
/// <para>
/// Проверка — одно соединение с www.google.com: его сертификат выпускает только Google
/// Trust Services (или GlobalSign, старый корень). Чужой издатель — значит, соединение
/// вскрыли на этом компьютере или по дороге.
/// </para>
/// </remarks>
public static class TlsInterception
{
    private static readonly string[] GoogleIssuers = ["Google Trust Services", "GTS ", "GlobalSign"];

    private static readonly (string Mark, string Product, string Advice)[] Known =
    [
        ("Kaspersky", "Kaspersky", "Kaspersky → Настройки → Сетевые параметры → «Не проверять защищённые соединения» (или добавьте папку Zephyron в исключения)"),
        ("ESET", "ESET", "ESET → Настройка → Защита доступа в Интернет → выключите «SSL/TLS»"),
        ("Dr.Web", "Dr.Web", "Dr.Web → Центр безопасности → Файлы и сеть → SpIDer Gate → «Не проверять зашифрованный трафик»"),
        ("Avast", "Avast", "Avast → Меню → Настройки → Защита → Основные компоненты → Веб-экран → снимите «Включить сканирование HTTPS»"),
        ("AVG", "AVG", "AVG → Меню → Настройки → Основная защита → Веб-экран → снимите «Включить сканирование HTTPS»"),
        ("Bitdefender", "Bitdefender", "Bitdefender → Защита → Онлайн-защита → выключите «Шифрованное веб-сканирование»"),
        ("AdGuard", "AdGuard", "AdGuard → Настройки → Сеть → выключите «Фильтровать HTTPS-протокол»"),
        ("COMODO", "Comodo", "Comodo → Настройки → выключите проверку HTTPS (SSL) в веб-фильтре"),
        ("Avira", "Avira", "Avira → Безопасность → Веб-защита → выключите проверку HTTPS"),
        ("Norton", "Norton", "Norton → Настройки → Брандмауэр → выключите «Проверку зашифрованного трафика»"),
        ("Fiddler", "Fiddler", "закройте Fiddler или выключите в нём «Decrypt HTTPS traffic»"),
    ];

    /// <summary>Перехвачено ли соединение с Google; <c>null</c> — нет или проверить не удалось.</summary>
    public static async Task<Interception?> CheckAsync(CancellationToken cancellationToken)
    {
        string? issuer = null;

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("www.google.com", 443, cancellationToken).ConfigureAwait(false);

            // Сертификат берём как есть, даже не доверенный: важно, кто его подписал, а не верен ли он.
            using var tls = new SslStream(tcp.GetStream(), false, (_, cert, _, _) =>
            {
                issuer = (cert as X509Certificate2 ?? (cert is null ? null : new X509Certificate2(cert)))?.Issuer;
                return true;
            });

            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "www.google.com" }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Нет сети или порт закрыт — о перехвате судить нечем.
        }

        return issuer is null ? null : Judge(issuer);
    }

    /// <summary>Издатель сертификата «Google» → перехват или <c>null</c>.</summary>
    internal static Interception? Judge(string issuer)
    {
        if (GoogleIssuers.Any(g => issuer.Contains(g, StringComparison.OrdinalIgnoreCase)))
            return null;

        foreach (var (mark, product, advice) in Known)
        {
            if (issuer.Contains(mark, StringComparison.OrdinalIgnoreCase))
                return new(issuer, product, advice);
        }

        return new(issuer, null, "найдите в антивирусе или фильтре трафика «проверку HTTPS / защищённых соединений» и выключите её");
    }
}
