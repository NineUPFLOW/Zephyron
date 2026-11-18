using System.Net;
using Zephyron.Proxy;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>Узел, молчащий на запросы плеера: когда отказ, а когда нет.</summary>
public sealed class VideoNodeHangTests
{
    private const string Cache = "rr2---sn-8ph2xajvh-nufl.googlevideo.com";
    private const string Google = "rr3---sn-aj5go5-53.googlevideo.com";
    private static readonly DateTime T0 = new(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);

    private static HangTracker.Sample S(uint port, string host, ulong sent, ulong received) =>
        new(new HangTracker.Key(1, port, host == Cache ? 2u : 3u, 443), host, sent, received);

    /// <summary>Два запроса к кэшу без ответа, а Google в это время отдаёт видео.</summary>
    private static IReadOnlyList<string> TwoSilentRequests(HangTracker tracker, bool googleDelivers)
    {
        IReadOnlyList<string> due = [];
        ulong google = 0;

        // Прогрев: соединения уже идут, ничего не ждут.
        for (int w = 0; w < 130; w++)
            tracker.Observe([S(20, Google, 3000, 1000 + (ulong)w * (googleDelivers ? 60_000u : 0u))], T0.AddMilliseconds(250 * w));

        google = googleDelivers ? 130 * 60_000u : 0u;

        for (int i = 0; i <= 40; i++)
        {
            var now = T0.AddMilliseconds(250 * (130 + i));
            google += googleDelivers ? 60_000u : 0u;
            // Запрос на 1-м и на 20-м замере, ответа нет.
            ulong sent = i >= 20 ? 9000u : i >= 1 ? 5000u : 1000u;
            due = tracker.Observe([S(10, Cache, sent, 1290), S(20, Google, 3000, 1000 + google)], now);

            if (due.Count > 0)
                return due;
        }

        return due;
    }

    [Fact]
    public void SilentCacheIsRefusedWhenAnotherNodeDelivers()
    {
        Assert.Equal([Cache], TwoSilentRequests(new HangTracker(), googleDelivers: true));
    }

    [Fact]
    public void TheOnlyPathIsNeverRefused()
    {
        Assert.Empty(TwoSilentRequests(new HangTracker(), googleDelivers: false));
    }

    [Fact]
    public void NothingIsRefusedRightAfterEnginesStart()
    {
        var tracker = new HangTracker();
        ulong google = 0;

        for (int i = 0; i <= 40; i++)
        {
            google += 60_000;
            ulong sent = i >= 20 ? 9000u : i >= 1 ? 5000u : 1000u;
            Assert.Empty(tracker.Observe([S(10, Cache, sent, 1290), S(20, Google, 3000, 1000 + google)], T0.AddMilliseconds(250 * i)));
        }
    }

    [Fact]
    public void AnIdleConnectionSeenFirstIsNotAHang()
    {
        var tracker = new HangTracker();

        for (int i = 0; i < 40; i++)
            Assert.Empty(tracker.Observe([S(10, Cache, 30_000, 1_900_000), S(20, Google, 3000, 1000 + (ulong)i * 60_000)], T0.AddMilliseconds(250 * i)));
    }

    [Fact]
    public void RequestAnsweredWithinOneSampleIsNotAHang()
    {
        var tracker = new HangTracker();
        ulong sent = 1000, received = 1000;

        for (int i = 0; i < 80; i++)
        {
            // Каждый замер: запрос и ответ вместе.
            sent += 4000;
            received += 500_000;
            Assert.Empty(tracker.Observe([S(10, Cache, sent, received), S(20, Google, 3000, 1000 + (ulong)i * 60_000)], T0.AddMilliseconds(250 * i)));
        }
    }

    [Fact]
    public void HangRefusalIsShortAndNotRechecked()
    {
        var now = DateTimeOffset.Now;
        var state = new Dictionary<string, VideoNodeGuard.Entry>
        {
            [Cache] = new("194.154.65.205", now - VideoNodeGuard.HangHold - TimeSpan.FromSeconds(1), now - VideoNodeGuard.Recheck, Hang: true),
            ["rr1---sn-x.googlevideo.com"] = new("1.2.3.4", now - TimeSpan.FromMinutes(5), now - TimeSpan.FromMinutes(5), Hang: true),
        };

        var (recheck, expired) = VideoNodeGuard.Due(state, now);

        Assert.Empty(recheck);
        Assert.Equal([Cache], expired);
    }
}
