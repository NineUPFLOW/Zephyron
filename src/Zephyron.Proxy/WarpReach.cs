using Zephyron.Subscriptions;

namespace Zephyron.Proxy;

/// <summary>
/// Похоже ли, что WARP у этого провайдера не достучаться, — по истории замеров выхода.
/// </summary>
/// <remarks>
/// Свои замеры, а не отдельная проба: сторож и вкладка VPN и так мерят выход WARP.
/// Нужны три ответа подряд «нет» и ни одного «да» за недавнее время — один промах
/// бывает у любого сервера, а старая тишина ничего не говорит о нынешнем дне.
/// </remarks>
public static class WarpReach
{
    public const int MinimumChecks = 3;

    public static readonly TimeSpan Fresh = TimeSpan.FromHours(1);

    public static bool LooksBlocked(ServerHealth? health, DateTimeOffset? now = null)
    {
        if (health is null || health.Recent.Count < MinimumChecks || health.Recent.Any(ok => ok))
            return false;

        return (now ?? DateTimeOffset.Now) - health.CheckedAt <= Fresh;
    }

    public static bool LooksBlocked(string? healthPath = null) =>
        LooksBlocked(ServerHealthCache.Load(healthPath).Find(Warp.MasqueTag));
}
