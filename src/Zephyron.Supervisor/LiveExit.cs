using Zephyron.Proxy;

namespace Zephyron.Supervisor;

/// <summary>
/// Смена закреплённого сервера на работающем движке — без перезапуска и обрыва соединений.
/// </summary>
public static class LiveExit
{
    /// <summary>Годится ли тег для живой смены: он есть среди выходов основной группы.</summary>
    public static bool CanSelect(IReadOnlyList<string>? members, string tag) =>
        members is not null && members.Contains(tag);

    /// <summary>
    /// Ставит выход в основной группе sing-box и сообщает супервизору новый выбор.
    /// </summary>
    /// <param name="tag">Тег сервера; <c>null</c> — автоподбор.</param>
    /// <returns><c>false</c> — движки не работают или такого выхода в запущенном конфиге нет: нужен перезапуск.</returns>
    public static async Task<bool> SwitchAsync(string? tag, CancellationToken cancellationToken = default)
    {
        if (SupervisorState.Load(SupervisorState.DefaultPath) is not { } state || !state.IsSupervisorAlive())
            return false;

        using var api = new ClashApi(keys: EngineKeys.Current());

        var target = string.IsNullOrWhiteSpace(tag) ? SingBoxService.LatencyGroup : tag;

        // Тег берём из запущенного конфига, а не из файла: файл могли пересобрать после старта.
        if (!CanSelect(await api.MembersAsync(SingBoxService.SelectorGroup, cancellationToken), target))
            return false;

        if (!await SingBoxService.SelectMainAsync(api, target, cancellationToken))
            return false;

        // Сначала выбор, потом записка: иначе сторож успел бы вернуть старый сервер, не зная о смене.
        ExitPin.Write(ExitPin.DefaultPath, string.IsNullOrWhiteSpace(tag) ? null : tag);
        return true;
    }
}
