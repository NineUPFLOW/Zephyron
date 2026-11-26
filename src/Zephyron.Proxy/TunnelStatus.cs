using System.Net;
using System.Text.Json;
using Zephyron.Core;
using Zephyron.Subscriptions;

namespace Zephyron.Proxy;

/// <summary>
/// Обстановка, в которой снят замер: через что ходим и куда выходим.
/// </summary>
/// <remarks>
/// <para>
/// Отчёт без этого нечем сравнить с другим отчётом. Проверка снимает
/// не свойства сети, а поведение сети под конкретной настройкой, и два
/// прогона под разными пресетами дают разные строки — при том что оба
/// выглядят одинаково достоверно.
/// </para>
/// <para>
/// Лежит в библиотеке, а не во фронте, и это не вкусовщина. До 17.09 всё это
/// было приватными методами консольной команды, а окно не имело ничего —
/// ни состояния туннеля, ни страны выхода. Раз консоль решено убрать,
/// переносить её приватные методы в окно значило бы завести вторую копию
/// там же, где первая как раз и умирает. Общее место — единственный способ
/// не дать им разойтись.
/// </para>
/// </remarks>
public static class TunnelStatus
{
    /// <summary>Порт, на котором движок держит Clash API.</summary>
    public const int DefaultClashPort = 9090;

    /// <summary>
    /// Как подключённый сервер соотносится с выбором в настройках.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Прежде «закреплён» говорилось про всякий сервер, на который селектор
    /// указывает прямо. До 28.09 это и значило «выбран руками». Но с 28.09
    /// сторож сам ставит серверы прямо в селектор — при прогреве и при замене
    /// умершего, — и при автоподборе <c>zy status</c> и вкладка VPN стали
    /// писать «закреплён» про сервер, которого никто не выбирал.
    /// </para>
    /// <para>
    /// 30.09 это стоило неверного вывода: заминки туннеля я объяснил тем, что
    /// сторож не переключает закреплённый сервер, — а у владельца стоял
    /// автоподбор, и проверка подключённого сервера была выключена вовсе.
    /// Закреплён ли сервер, знают только настройки.
    /// </para>
    /// </remarks>
    /// <param name="server">Через что идёт трафик, по ответу движка.</param>
    /// <param name="preferred">Сервер, выбранный в настройках; пусто — автоподбор.</param>
    public static ExitStanding Standing(string? server, string? preferred) =>
        string.IsNullOrWhiteSpace(preferred)
            ? ExitStanding.Auto
            : server == preferred ? ExitStanding.Pinned : ExitStanding.Other;

    /// <summary>
    /// Как назвать выход в <c>zy status</c> и в отчёте: закреплён, автоподбор
    /// или временная замена — теми словами, что выведены из <see cref="Standing"/>.
    /// </summary>
    /// <param name="automatic">Селектор движка стоит на группе автоподбора, а не прямо на сервере.</param>
    /// <param name="remark">Примечание надзора к sing-box — им он называет замену выбранному серверу.</param>
    public static string StandingWord(string? server, bool automatic, string? preferred, string? remark) =>
        Standing(server, preferred) switch
        {
            ExitStanding.Pinned => "закреплён",
            ExitStanding.Other =>
                remark ?? $"в настройках выбран «{preferred}» — применится при запуске движков",
            _ => automatic ? "автоподбор" : "автоподбор, поставлен сторожем",
        };

    /// <summary>
    /// Строка «Сервер» на «Главной»: имя выхода, а при автоподборе — «(авто)» следом.
    /// </summary>
    /// <remarks>
    /// Владелец 01.10: прежде строка была «выбранное в настройках · сейчас
    /// живое», с флагом буквами — «IT Италия · сейчас Cloudflare WARP», — и при
    /// включённом WARP называла выбранным сервер подписки, которая на паузе.
    /// Теперь одно имя: живое, когда движок ответил, иначе выбранное
    /// (Warp.PreferredExit — при WARP это он). «(временно)» — когда движок
    /// держит не выбранный: замена молчащему либо выбор ещё не применён.
    /// </remarks>
    /// <param name="live">Через что идёт трафик по ответу движка; <c>null</c> — движки стоят или не ответили.</param>
    public static string ServerLine(AppSettings settings, string? live)
    {
        var preferred = Warp.PreferredExit(settings);

        if (live is null)
            return preferred is null ? "Автоподбор" : NameOf(preferred);

        return Standing(live, preferred) switch
        {
            ExitStanding.Auto => $"{NameOf(live)} (авто)",
            ExitStanding.Pinned => NameOf(live),
            _ => $"{NameOf(live)} (временно)",
        };
    }

    /// <summary>Имя сервера без флага: «🇮🇹 Италия» → «Италия».</summary>
    public static string NameOf(string tag) =>
        CountryTag.Split(tag).Name is { Length: > 0 } name ? name : tag;

    /// <summary>
    /// Через какой сервер движок ходит прямо сейчас.
    /// </summary>
    /// <remarks>
    /// Спрашивается у самого движка, а не берётся из настроек: в настройках
    /// стоит «авто», а работает в этот момент один определённый, и назвать
    /// надо его.
    /// </remarks>
    public static async Task<string?> CurrentServerAsync(
        CancellationToken cancellationToken,
        int? clashPort = null) =>
        (await CurrentExitAsync(cancellationToken, clashPort)).Server;

    /// <summary>
    /// Сервер прямо сейчас и выбран ли он автоподбором, а не закреплён.
    /// </summary>
    /// <remarks>
    /// Второе важно не меньше первого. 23.09 в настройках стояло «авто»,
    /// а движок держался WARP из своего кэша, и Telegram висел; заметить это
    /// можно было, только спросив движок, — теперь это видно в <c>zy status</c>.
    /// </remarks>
    public static async Task<(string? Server, bool Automatic)> CurrentExitAsync(
        CancellationToken cancellationToken,
        int? clashPort = null)
    {
        try
        {
            // Срок — на запрос, а клиент общий (<see cref="Clash"/>): «Главная» спрашивает раз в две секунды.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(3));
            cancellationToken = limit.Token;

            var http = Clash;
            int port = clashPort ?? EnginePorts.CurrentClashPort();

            var json = await GetAsync(http, $"http://127.0.0.1:{port}/proxies/auto", cancellationToken);

            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("now", out var now))
                return (null, false);

            var name = Named(now);

            // Селектор может указывать на группу, а не на сервер. Тогда
            // спрашиваем ещё раз у неё — иначе получится «auto-latency»,
            // что не ответ на вопрос «через что мы сейчас ходим».
            if (name is not null && name.StartsWith("auto", StringComparison.OrdinalIgnoreCase))
            {
                var inner = await GetAsync(
                    http, $"http://127.0.0.1:{port}/proxies/{Uri.EscapeDataString(name)}", cancellationToken);

                using var group = JsonDocument.Parse(inner);

                // Пустой ответ означает, что группа ещё не выбрала: она
                // из тех, что меряют задержку, и до первого замера ей выбирать
                // не из чего. Возвращаем имя самой группы, а не пустоту —
                // по нему хотя бы можно спросить состояние.
                return Remember((group.RootElement.TryGetProperty("now", out var chosen)
                    ? Named(chosen) ?? name
                    : name, true));
            }

            return Remember((name, false));
        }
        catch (Exception)
        {
            // Движок мог не поднять Clash API либо ещё не успеть. Незнание
            // честнее выдуманного имени сервера.
            return (null, false);
        }
    }

    /// <summary>
    /// Один клиент на опросы Clash API с «Главной».
    /// </summary>
    /// <remarks>
    /// Прежде каждый опрос заводил свой клиент с обработчиком и новым соединением —
    /// раз в две секунды, часами. Адрес — петля, прокси системы ему не нужен.
    /// </remarks>
    private static readonly HttpClient Clash = new(
        new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>
    /// Сколько байт движок отправил и принял с запуска — по итогам <c>/connections</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Тело ответа — список всех живых соединений (около 600 байт на каждое), а итоги
    /// стоят в его конце (замер 26.11: ключи по алфавиту, <c>connections</c> первым).
    /// Прежде тело читалось строкой и целиком разбиралось в <c>JsonNode</c> каждые две секунды:
    /// у того, кто держит тысячи соединений (торренты, десятки вкладок), это мегабайты
    /// строк и узлов за опрос, а буферы пула массивов от таких ответов остаются в памяти —
    /// окно росло на сотню мегабайт в минуту, и сборка мусора его не возвращала.
    /// Теперь тело читается потоком через 16 КБ буфера, соединения пропускаются, не
    /// превращаясь в объекты.
    /// </para>
    /// </remarks>
    public static async Task<(long Up, long Down)?> TotalsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(10));

            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{EnginePorts.CurrentClashListen()}/connections");
            EngineKeys.Authorize(request, EngineKeys.Current());

            using var response = await Clash.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);

            if (!response.IsSuccessStatusCode)
                return null;

            await using var body = await response.Content.ReadAsStreamAsync(limit.Token);

            var buffer = new byte[16 * 1024];
            int filled = 0;
            var state = default(JsonReaderState);
            int which = 0;
            long? up = null, down = null;

            while (true)
            {
                int read = await body.ReadAsync(buffer.AsMemory(filled), limit.Token);
                filled += read;

                int consumed = ScanTotals(buffer.AsSpan(0, filled), read == 0, ref state, ref which, ref up, ref down);

                if (up is { } sent && down is { } received)
                    return (sent, received);

                if (read == 0)
                    return null;

                // Недочитанное слово переезжает в начало буфера; не влезает — буфер растёт (до мегабайта).
                Buffer.BlockCopy(buffer, consumed, buffer, 0, filled - consumed);
                filled -= consumed;

                if (filled == buffer.Length)
                {
                    if (buffer.Length >= 1 << 20)
                        return null;

                    Array.Resize(ref buffer, buffer.Length * 2);
                }
            }
        }
        catch (Exception)
        {
            // Движок не отвечает или ответил не так — счётчиков нет, строка покажет прочерк.
            return null;
        }
    }

    /// <summary>Проход по куску ответа: берёт только <c>uploadTotal</c> и <c>downloadTotal</c> верхнего уровня.</summary>
    /// <returns>Сколько байт разобрано; остаток — начало слова, не уместившегося в кусок.</returns>
    private static int ScanTotals(
        ReadOnlySpan<byte> data, bool final, ref JsonReaderState state, ref int which, ref long? up, ref long? down)
    {
        var reader = new Utf8JsonReader(data, final, state);

        while (reader.Read())
        {
            if (reader.CurrentDepth != 1)
                continue;

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                which = reader.ValueTextEquals("uploadTotal"u8) ? 1 : reader.ValueTextEquals("downloadTotal"u8) ? 2 : 0;
            }
            else if (reader.TokenType == JsonTokenType.Number && which != 0)
            {
                if (which == 1)
                    up = reader.GetInt64();
                else
                    down = reader.GetInt64();

                which = 0;
            }
        }

        state = reader.CurrentState;

        return (int)reader.BytesConsumed;
    }

    /// <summary>Отмечает выход в журнале недавних (ExitHistory) и отдаёт ответ как есть.</summary>
    /// <remarks>
    /// Служебные имена — не выходы: группа, ещё не выбравшая сервер, и обход
    /// мимо туннеля (<c>direct</c>).
    /// </remarks>
    private static (string? Server, bool Automatic) Remember((string? Server, bool Automatic) answer)
    {
        if (answer.Server is { } server
            && !server.StartsWith("auto", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(server, "direct", StringComparison.OrdinalIgnoreCase))
        {
            ExitHistory.Note(server, DateTimeOffset.Now);
        }

        return answer;
    }

    /// <summary>Запрос к Clash API с секретом движка (с 27.09 без него — 401).</summary>
    private static async Task<string> GetAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        EngineKeys.Authorize(request, EngineKeys.Current());

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>
    /// Имя из ответа движка; пустая строка считается отсутствием имени.
    /// </summary>
    /// <remarks>
    /// Разница видна в отчёте. Группа, меряющая задержку, до первого замера
    /// отвечает пустым <c>now</c>, а не отсутствующим, — и проверка, начатая
    /// сразу после запуска движков, писала в шапку «Сервер: » с пустотой
    /// после двоеточия и «Туннель: состояние не выяснено». Замер 17.09 17:44:
    /// движки подняты в 17:43:28, отчёт снят через полминуты, а в 17:46 та же
    /// группа уже называла «Германия — TLS XHTTP».
    /// </remarks>
    private static string? Named(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            && element.GetString() is { Length: > 0 } name
            && !string.IsNullOrWhiteSpace(name)
                ? name
                : null;

    /// <summary>
    /// Проходит ли трафик через названный выход.
    /// </summary>
    /// <remarks>
    /// Спрашивается у самого движка его же диагностикой: он отвечает 200
    /// с задержкой, когда проба дошла, и 5xx, когда не дошла. Разбирать тело
    /// незачем — сам факт неуспеха и означает, что через этот выход трафик
    /// не идёт.
    /// </remarks>
    public static async Task<TunnelState> StateAsync(
        string? server,
        CancellationToken cancellationToken,
        int? clashPort = null)
    {
        // Имени нет — спрашиваем селектор. Он есть всегда, пока движок жив,
        // и запрос задержки к нему заставляет группу выбрать. Прежде здесь
        // возвращалось «состояние не выяснено», и отчёт, снятый сразу после
        // запуска движков, молчал о туннеле при живом туннеле.
        var target = string.IsNullOrWhiteSpace(server) ? "auto" : server;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

            var url = $"http://127.0.0.1:{clashPort ?? EnginePorts.CurrentClashPort()}/proxies/{Uri.EscapeDataString(target)}/delay"
                + "?timeout=5000&url=" + Uri.EscapeDataString("https://www.gstatic.com/generate_204");

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            EngineKeys.Authorize(request, EngineKeys.Current());

            using var response = await http.SendAsync(request, cancellationToken);

            return response.IsSuccessStatusCode ? TunnelState.Alive : TunnelState.Dead;
        }
        catch (Exception)
        {
            // Движок мог не поднять Clash API. Это незнание, а не приговор:
            // объявить туннель мёртвым по недоступности его же диагностики
            // значило бы обвинить исправную трубу.
            return TunnelState.Unknown;
        }
    }

    /// <summary>Имя, по которому проверяется внешний адрес.</summary>
    private const string ExitProbe = "api.ipify.org";

    /// <summary>
    /// Внешний адрес, страна и то, через что всё это получено.
    /// </summary>
    /// <remarks>
    /// Третье не менее важно первых двух. С поднятым туннелем ответ службы
    /// определения адреса может прийти и по домашнему каналу, и тогда
    /// «страна выхода» описывает провайдера, а не сервер подписки.
    /// </remarks>
    public static async Task<ExitReading> ReadExitAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };

            var address = (await http.GetStringAsync($"https://{ExitProbe}", cancellationToken)).Trim();
            var country = (await http.GetStringAsync("https://ipinfo.io/country", cancellationToken)).Trim();

            return new ExitReading
            {
                Address = address,
                Country = country,
                Tunnelled = await WentThroughTunnelAsync(ExitProbe, cancellationToken),
            };
        }
        catch (Exception)
        {
            // Не выяснилось — молчим. Отсутствие сведений хуже ложных,
            // но ложные здесь стоили бы смены рабочего сервера ни за чем.
            return new ExitReading { Tunnelled = false };
        }
    }

    /// <summary>Разрешается ли имя в fakeip — то есть уйдёт ли оно в туннель.</summary>
    private static async Task<bool> WentThroughTunnelAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);

            return addresses.Any(a => TunnelHealth.IsFakeIp(a));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Путь к движку sing-box рядом с программой.
    /// </summary>
    /// <remarks>
    /// Нужен тем проверкам, которые поднимают свой экземпляр движка вместо
    /// того, чтобы трогать работающий, — см. <see cref="TunnelReach"/>.
    /// Подъём по каталогам нужен только разработчику: в поставке первая же
    /// проверка попадает в цель.
    /// </remarks>
    public static string? FindSingBox()
    {
        var beside = Path.Combine(Zephyron.Core.InstallRoot.AppFolder, "engines", "sing-box", "sing-box.exe");

        if (File.Exists(beside))
            return beside;

        var directory = new DirectoryInfo(Zephyron.Core.InstallRoot.AppFolder);

        while (directory is not null)
        {
            try
            {
                var candidate = Directory
                    .GetFiles(directory.FullName, "sing-box.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (candidate is not null)
                    return candidate;
            }
            catch (Exception)
            {
                // Каталог без доступа — не повод прекращать поиск выше.
            }

            directory = directory.Parent;
        }

        return null;
    }
}

/// <summary>Как подключённый сервер соотносится с выбором в настройках.</summary>
public enum ExitStanding
{
    /// <summary>В настройках автоподбор — сервер выбран им либо поставлен сторожем.</summary>
    Auto,

    /// <summary>Тот самый сервер, что выбран руками.</summary>
    Pinned,

    /// <summary>
    /// Выбран один, а трафик идёт через другой: временная замена либо выбор,
    /// который применится при следующем запуске движков.
    /// </summary>
    Other,
}
