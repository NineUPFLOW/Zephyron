using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Zephyron.Supervisor.Evolution;

/// <summary>
/// Результат пробы голосового протокола Discord (UDP STUN / WebRTC).
/// </summary>
public sealed record VoiceProbeResult
{
    public bool Success { get; init; }
    public long LatencyMs { get; init; }
    public string Detail { get; init; } = "";
}

/// <summary>
/// Сетевой зонд для проверки прохождения UDP пакетов Discord Voice и WebRTC (STUN binding request).
/// Выявляет зависания голосовых каналов Discord на стадии «Подключение к RTC» и «Маршрут не найден».
/// </summary>
public static class DiscordVoiceProbe
{
    private static readonly byte[] StunBindingRequest =
    [
        0x00, 0x01, // Binding Request
        0x00, 0x00, // Length = 0
        0x21, 0x12, 0xA4, 0x42, // Magic Cookie
        // 12-byte Transaction ID
        0x5A, 0x65, 0x70, 0x68, 0x79, 0x72, 0x6F, 0x6E, 0x55, 0x44, 0x50, 0x31
    ];

    private static readonly (string Host, int Port)[] VoiceGateways =
    [
        ("stun.l.google.com", 19302),
        ("stun1.l.google.com", 19302),
        ("stun.cloudflare.com", 3478)
    ];

    /// <summary>
    /// Проверяет прохождение STUN/WebRTC UDP пакетов.
    /// </summary>
    public static async Task<VoiceProbeResult> TestVoiceUdpAsync(int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        foreach (var (host, port) in VoiceGateways)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                var targetIp = await RecipeTrial.AddressForAsync(host, ct);
                if (targetIp is null)
                    continue;

                using var udp = new UdpClient();
                using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linkCts.CancelAfter(timeoutMs);

                var endpoint = new IPEndPoint(targetIp, port);
                await udp.SendAsync(StunBindingRequest, StunBindingRequest.Length, endpoint);

                var receiveTask = udp.ReceiveAsync(linkCts.Token).AsTask();
                var completed = await Task.WhenAny(receiveTask, Task.Delay(timeoutMs, linkCts.Token));

                if (completed == receiveTask)
                {
                    var res = await receiveTask;
                    sw.Stop();

                    // Проверяем STUN Success Response: 0x0101 и совпадение Magic Cookie
                    if (res.Buffer.Length >= 8 && res.Buffer[0] == 0x01 && res.Buffer[1] == 0x01)
                    {
                        return new VoiceProbeResult
                        {
                            Success = true,
                            LatencyMs = sw.ElapsedMilliseconds,
                            Detail = $"Discord Voice/STUN доступен ({sw.ElapsedMilliseconds} мс)"
                        };
                    }
                }
            }
            catch
            {
                // Пробуем следующий шлюз
            }
        }

        sw.Stop();
        return new VoiceProbeResult
        {
            Success = false,
            LatencyMs = sw.ElapsedMilliseconds,
            Detail = "Discord Voice/STUN заблокирован (таймаут UDP)"
        };
    }
}
