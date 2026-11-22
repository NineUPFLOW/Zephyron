using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Zephyron.Supervisor.Evolution;

/// <summary>
/// Результат L7-пробы потоковой пропускной способности (throughput).
/// </summary>
public sealed record ThroughputResult
{
    public bool Success { get; init; }
    public double SpeedKBps { get; init; }
    public long LatencyMs { get; init; }
    public long BytesTransferred { get; init; }
    public bool IsThrottled { get; init; }
    public string Detail { get; init; } = "";
}

/// <summary>
/// Сетевой L7-зонд для проверки реальной пропускной способности потоковых сервисов (YouTube/Googlevideo, Twitch).
/// Обнаруживает замедление (throttling) со стороны ТСПУ после рукопожатия TLS.
/// </summary>
public static class L7ThroughputProbe
{
    private const int TargetTestBytes = 256 * 1024; // 256 КБ
    private const double ThrottleThresholdKBps = 180.0; // Порог замедления (ниже 180 КБ/с = замедлено DPI)

    /// <summary>
    /// Проверяет реальную скорость скачивания чанка данных с целевого видео/медиа хоста.
    /// </summary>
    public static async Task<ThroughputResult> MeasureSpeedAsync(
        string host,
        IPAddress address,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long totalBytesRead = 0;

        try
        {
            using var client = new TcpClient();
            using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            await client.ConnectAsync(address, 443, linkCts.Token);
            long connectMs = sw.ElapsedMilliseconds;

            await using var sslStream = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, _, _, _) => true);

            await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host
            }, linkCts.Token);

            long handshakeMs = sw.ElapsedMilliseconds;

            // Формируем L7 HTTP/1.1 запрос с Range-запросом фрагмента видео
            string path = host.Contains("googlevideo", StringComparison.OrdinalIgnoreCase)
                ? "/videoplayback?mime=video%2Fmp4&range=0-262143"
                : "/";

            var request = $"GET {path} HTTP/1.1\r\n" +
                          $"Host: {host}\r\n" +
                          $"User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) Zephyron/1.0\r\n" +
                          $"Accept: */*\r\n" +
                          $"Range: bytes=0-{TargetTestBytes - 1}\r\n" +
                          $"Connection: close\r\n\r\n";

            var requestBytes = Encoding.ASCII.GetBytes(request);
            await sslStream.WriteAsync(requestBytes, linkCts.Token);
            await sslStream.FlushAsync(linkCts.Token);

            // Читаем тело ответа и замеряем время передачи полезной нагрузки
            var buffer = new byte[16 * 1024];
            var streamSw = Stopwatch.StartNew();

            while (totalBytesRead < TargetTestBytes)
            {
                int read = await sslStream.ReadAsync(buffer, linkCts.Token);
                if (read <= 0)
                    break;

                totalBytesRead += read;

                // Если поток данных читается слишком медленно (DPI throttling stall)
                if (streamSw.ElapsedMilliseconds > 6000 && totalBytesRead < 32 * 1024)
                {
                    break;
                }
            }

            streamSw.Stop();
            sw.Stop();

            double elapsedSec = Math.Max(streamSw.Elapsed.TotalSeconds, 0.05);
            double speedKBps = (totalBytesRead / 1024.0) / elapsedSec;
            bool isThrottled = totalBytesRead > 0 && speedKBps < ThrottleThresholdKBps;

            string detail = totalBytesRead >= 16 * 1024
                ? $"Скорость: {speedKBps:F0} КБ/с ({totalBytesRead / 1024} КБ)"
                : (isThrottled ? $"ТСПУ замедление ({speedKBps:F0} КБ/с)" : $"Передано {totalBytesRead / 1024} КБ");

            return new ThroughputResult
            {
                Success = totalBytesRead >= 16 * 1024 && !isThrottled,
                SpeedKBps = speedKBps,
                LatencyMs = handshakeMs,
                BytesTransferred = totalBytesRead,
                IsThrottled = isThrottled,
                Detail = detail
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ThroughputResult
            {
                Success = false,
                SpeedKBps = 0,
                LatencyMs = sw.ElapsedMilliseconds,
                BytesTransferred = totalBytesRead,
                IsThrottled = false,
                Detail = RecipeTrial.Short(ex)
            };
        }
    }
}
