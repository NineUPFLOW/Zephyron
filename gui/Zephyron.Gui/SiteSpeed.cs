using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Zephyron.Gui;

/// <summary>Где оборвался замер до сайта.</summary>
internal enum SiteFail { None, Dns, Tcp, Tls, Timeout, Other }

/// <summary>Итог замера до сайта; <c>null</c> в поле — этапа не было или он не измерим.</summary>
internal sealed record SiteResult
{
    public string Host { get; init; } = string.Empty;
    public bool Tunnel { get; init; }
    public string? Ip { get; init; }
    public double? DnsMs { get; init; }
    public double? TcpMs { get; init; }
    public double? TlsMs { get; init; }
    public double? TtfbMs { get; init; }
    public double? DownMbps { get; init; }
    public long Bytes { get; init; }

    /// <summary>От запроса имени до последнего байта, мс.</summary>
    public double? TotalMs { get; init; }

    public string? Http { get; init; }
    public bool H3Hint { get; init; }
    public string? Server { get; init; }

    /// <summary>Разброс времени соединения (стандартное отклонение), мс.</summary>
    public double? JitterMs { get; init; }

    /// <summary>Рост времени соединения под нагрузкой над простоем, мс.</summary>
    public double? LoadedMs { get; init; }

    public char? Grade { get; init; }
    public SiteFail Fail { get; init; }
}

/// <param name="Status">«DNS…», «Соединяюсь…» — что идёт сейчас.</param>
internal sealed record SiteProgress(string Status, SiteResult Partial);

/// <summary>
/// Замер пути до одного сайта: DNS, TCP, TLS, ответ и загрузка страницы, джиттер и задержка под нагрузкой.
/// </summary>
/// <remarks>
/// <para>
/// Напрямую всё меряется на одном настоящем соединении: TCP — в ConnectCallback,
/// TLS — между ним и PlaintextStreamFilter (поток уже зашифрован, запрос ещё не ушёл),
/// ответ — от фильтра до заголовков. Через туннель (прокси движка) DNS, TCP и TLS
/// происходят по ту сторону и не разделяются — их нет, а не нули.
/// </para>
/// <para>
/// Джиттер и «под нагрузкой» — по времени установки TCP-соединения с тем же адресом:
/// пять замеров в покое и столько же на ходу, пока качается страница. Страница
/// короче секунды нагрузки не даёт — класс тогда не ставится, а не выдумывается.
/// </para>
/// </remarks>
internal static class SiteSpeed
{
    /// <summary>Тайм-аут одного шага (имя, соединение, ответ).</summary>
    internal static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Сколько страницы качаем: 10 МБ или десять секунд.</summary>
    private const long Cap = 10 * 1024 * 1024;
    private static readonly TimeSpan DownloadLimit = TimeSpan.FromSeconds(10);

    /// <summary>Меньше этого скорость загрузки ничего не говорит: время уходит на разгон, а не на канал.</summary>
    internal const long MeaningfulBytes = 100 * 1024;

    /// <summary>Адрес из поля ввода: без пробелов, со схемой; <c>null</c> — не адрес.</summary>
    internal static Uri? Normalize(string? input)
    {
        var text = string.Concat((input ?? string.Empty).Where(c => !char.IsWhiteSpace(c)));

        if (text.Length == 0)
            return null;

        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;

        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && uri.Host.Length > 0
            && Uri.CheckHostName(uri.Host) != UriHostNameType.Unknown
            ? uri
            : null;
    }

    /// <summary>Класс A–F по росту задержки под нагрузкой, мс: &lt;5, &lt;30, &lt;60, &lt;200, &lt;400, иначе F.</summary>
    internal static char Grade(double increaseMs) =>
        increaseMs < 5 ? 'A' : increaseMs < 30 ? 'B' : increaseMs < 60 ? 'C' : increaseMs < 200 ? 'D' : increaseMs < 400 ? 'E' : 'F';

    /// <summary>Стандартное отклонение; меньше двух значений — <c>null</c>.</summary>
    internal static double? StdDev(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
            return null;

        double mean = values.Average();

        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;

        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static double Ms(long from, long to) => Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;

    private sealed class StepFailed(SiteFail fail) : Exception { public SiteFail Fail { get; } = fail; }

    /// <summary>Шаг с тайм-аутом: сорвался по времени — Timeout, иначе — переданная причина.</summary>
    private static async Task<T> Within<T>(SiteFail onError, CancellationToken ct, Func<CancellationToken, Task<T>> work)
    {
        using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
        step.CancelAfter(StepTimeout);

        try
        {
            return await work(step.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new StepFailed(SiteFail.Timeout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not StepFailed)
        {
            throw new StepFailed(onError);
        }
    }

    /// <summary>Время установки TCP-соединения, мс; <c>null</c> — не вышло.</summary>
    private static async Task<double?> ConnectTimeAsync(IPAddress ip, int port, CancellationToken ct)
    {
        using var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        var start = Stopwatch.GetTimestamp();

        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(3));
            await socket.ConnectAsync(new IPEndPoint(ip, port), limit.Token);

            return Ms(start, Stopwatch.GetTimestamp());
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <param name="proxy"><c>null</c> — напрямую; иначе вход движка (через туннель).</param>
    internal static async Task<SiteResult> RunAsync(Uri uri, IWebProxy? proxy, IProgress<SiteProgress>? progress, CancellationToken ct)
    {
        var r = new SiteResult { Host = uri.IdnHost, Tunnel = proxy is not null };

        void Report(string status) => progress?.Report(new SiteProgress(status, r));

        try
        {
            IPAddress? ip = null;

            if (proxy is null)
            {
                Report("DNS…");

                var dnsStart = Stopwatch.GetTimestamp();

                var addresses = await Within(SiteFail.Dns, ct, token => Dns.GetHostAddressesAsync(uri.IdnHost, token));
                ip = addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).FirstOrDefault()
                    ?? throw new StepFailed(SiteFail.Dns);

                r = r with { Ip = ip.ToString(), DnsMs = Ms(dnsStart, Stopwatch.GetTimestamp()) };
                Report("Соединяюсь…");

                // Покой: несколько соединений до скачивания — основа джиттера и «под нагрузкой».
            }

            var idle = new List<double>();

            if (ip is not null)
            {
                for (int i = 0; i < 5; i++)
                {
                    if (await ConnectTimeAsync(ip, uri.Port, ct) is { } ms)
                        idle.Add(ms);

                    await Task.Delay(80, ct);
                }

                r = r with { JitterMs = StdDev(idle) };
                Report("Соединяюсь…");
            }

            long tcpStart = 0, tcpEnd = 0, plain = 0;
            Version? negotiated = null;

            using var handler = new SocketsHttpHandler
            {
                UseProxy = proxy is not null,
                Proxy = proxy,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
                AutomaticDecompression = DecompressionMethods.None,
                ConnectTimeout = StepTimeout,
            };

            if (proxy is null)
            {
                handler.ConnectCallback = async (context, token) =>
                {
                    // Адрес уже разрешён и измерен; после редиректа на другое имя — свой запрос.
                    var target = context.DnsEndPoint.Host.Equals(uri.IdnHost, StringComparison.OrdinalIgnoreCase)
                        ? ip!
                        : (await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token))[0];

                    var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

                    try
                    {
                        if (tcpEnd == 0)
                            tcpStart = Stopwatch.GetTimestamp();

                        await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), token);

                        if (tcpEnd == 0)
                            tcpEnd = Stopwatch.GetTimestamp();

                        return new NetworkStream(socket, true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                };
            }

            handler.PlaintextStreamFilter = (context, token) =>
            {
                if (plain == 0)
                {
                    plain = Stopwatch.GetTimestamp();
                    negotiated = context.NegotiatedHttpVersion;
                    Report("Жду ответ…");
                }

                return new ValueTask<Stream>(context.PlaintextStream);
            };

            using var client = new HttpClient(handler, false) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, uri) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Zephyron");

            var begin = Stopwatch.GetTimestamp();
            using var response = await RequestAsync();

            var headersAt = Stopwatch.GetTimestamp();

            async Task<HttpResponseMessage> RequestAsync()
            {
                using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
                step.CancelAfter(StepTimeout);

                try
                {
                    return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, step.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new StepFailed(SiteFail.Timeout);
                }
                catch (HttpRequestException)
                {
                    // Где оборвалось, видно по тому, до какого этапа дошли.
                    throw new StepFailed(proxy is null && tcpEnd == 0 ? SiteFail.Tcp : plain == 0 && uri.Scheme == "https" && tcpEnd != 0 ? SiteFail.Tls : SiteFail.Other);
                }
            }

            double? tcp = tcpEnd != 0 ? Ms(tcpStart, tcpEnd) : null;
            double? tls = uri.Scheme == "https" && tcpEnd != 0 && plain != 0 ? Ms(tcpEnd, plain) : null;

            r = r with
            {
                TcpMs = tcp,
                TlsMs = tls,
                TtfbMs = Ms(plain != 0 ? plain : begin, headersAt),
                Http = negotiated?.Major switch { 3 => "HTTP/3", 2 => "HTTP/2", 1 => "HTTP/1.1", _ => response.Version.Major == 2 ? "HTTP/2" : "HTTP/1.1" },
                H3Hint = response.Headers.TryGetValues("Alt-Svc", out var alt) && alt.Any(v => v.Contains("h3", StringComparison.OrdinalIgnoreCase)),
                Server = response.Headers.Server.ToString() is { Length: > 0 } server ? server : null,
            };

            Report("Скачиваю…");

            // Загрузка; рядом — соединения до того же адреса: что с задержкой, пока канал занят.
            var loaded = new List<double>();
            long total = 0;
            var read = Stopwatch.StartNew();

            using var download = CancellationTokenSource.CreateLinkedTokenSource(ct);
            download.CancelAfter(DownloadLimit);

            var sampler = ip is null ? Task.CompletedTask : Task.Run(async () =>
            {
                try
                {
                    while (!download.IsCancellationRequested)
                    {
                        if (await ConnectTimeAsync(ip, uri.Port, download.Token) is { } ms)
                            loaded.Add(ms);

                        await Task.Delay(250, download.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Загрузка кончилась — замеры тоже.
                }
            }, CancellationToken.None);

            try
            {
                await using var body = await response.Content.ReadAsStreamAsync(download.Token);
                var buffer = new byte[81920];
                int n;

                while ((n = await body.ReadAsync(buffer, download.Token)) > 0)
                {
                    total += n;

                    if (total >= Cap)
                        break;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Упёрлись в десять секунд: скачанное — замер, а не ошибка.
            }
            catch (Exception) when (total == 0)
            {
                throw new StepFailed(SiteFail.Other);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Оборвали посреди страницы: что успели скачать — то и замер.
            }

            read.Stop();
            await download.CancelAsync();
            await sampler;
            ct.ThrowIfCancellationRequested();

            double seconds = Math.Max(read.Elapsed.TotalSeconds, 0.001);
            double? increase = idle.Count >= 3 && loaded.Count >= 3 && seconds >= 1
                ? Math.Max(0, Median(loaded) - Median(idle))
                : null;

            r = r with
            {
                Bytes = total,
                DownMbps = total >= MeaningfulBytes ? total * 8 / seconds / 1_000_000 : null,
                TotalMs = (r.DnsMs ?? 0) + Ms(begin, Stopwatch.GetTimestamp()),
                LoadedMs = increase,
                Grade = increase is { } inc ? Grade(inc) : null,
            };

            Report("Готово");
            return r;
        }
        catch (StepFailed failed)
        {
            return r with { Fail = failed.Fail };
        }
    }
}
