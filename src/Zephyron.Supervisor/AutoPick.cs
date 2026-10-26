using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Zephyron.Core;
using Zephyron.Core.Connections;
using Zephyron.Core.Rules;
using Zephyron.Proxy;
using Zephyron.Subscriptions;
using Zephyron.Zapret;

namespace Zephyron.Supervisor;

/// <summary>Что подбираем: имя и подпись, под которой его видит человек.</summary>
/// <param name="List">
/// Список части сервиса, откуда взято имя; маршрут пишется в него, а не своим правилом
/// на <c>*.имя</c>. <c>null</c> — своё имя: ему и пишется правило на домен.
/// </param>
/// <param name="Within">Более широкий список, раньше правил которого встаёт запись (<c>ServicePart.Within</c>).</param>
/// <param name="DefaultOff">Часть выключена заводской настройкой (<c>ServicePart.DefaultOff</c>).</param>
public sealed record AutoPickTarget(
    string Host, string Label, string? List = null, string? Within = null, bool DefaultOff = false);

/// <summary>Способ открыть имя; порядок значений — порядок предпочтения.</summary>
public enum AutoPickWay { Direct, Preset, Recipe, Pin, Vpn }

/// <summary>Один опробованный способ и чем он кончился.</summary>
/// <param name="Recipe">Имя рецепта (для <see cref="AutoPickWay.Recipe"/>), как оно ляжет в правило.</param>
/// <param name="Title">Как назвать способ человеку: приёмы рецепта либо секция пресета.</param>
/// <param name="Server">Через какой сервер подписки (для VPN).</param>
/// <param name="Status">Код ответа сервера; <c>null</c> — ответа не было.</param>
/// <param name="CertValid">Сертификат действителен для имени; <c>null</c> — не знаем (VPN, нет ответа).</param>
/// <param name="Stub">Ответила заглушка отказа по стране (<see cref="RecipeTrial.Flow.Stub"/>).</param>
/// <param name="Address">Адрес, к которому прибивается имя (для <see cref="AutoPickWay.Pin"/>).</param>
public sealed record AutoPickTry(
    AutoPickWay Way,
    string? Recipe,
    string? Title,
    string? Server,
    bool Works,
    TimeSpan Took,
    string Detail,
    int? Status = null,
    bool? CertValid = null,
    bool Stub = false,
    string? Address = null)
{
    /// <summary>
    /// Насколько хорошо открылось: 2 — сайт ответил, 1 — ответил отказом по стране
    /// (<see cref="Stub"/>), 0 — не дошли.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Владелец 26.10: netflix.com отвечает «403 Not Available» любым рецептом,
    /// и подбор считал его открытым. Теперь способ, где сайт ответил, бьёт способ,
    /// где он отказал по стране.
    /// </para>
    /// <para>
    /// Всё прочее — страница, перенаправление, 404 на корне CDN, проверка на робота —
    /// один уровень. Пробовали делить и их: проба VPN по перенаправлениям не ходит
    /// и видит 301, а прямая доходит до 403 проверки Cloudflare — и stackoverflow.com,
    /// npmjs.com, fandom.com уходили в VPN, открываясь напрямую (26.10).
    /// </para>
    /// </remarks>
    public int Level => !Works ? 0 : Stub ? 1 : 2;
}

/// <summary>Что решено записать имени.</summary>
/// <param name="Recipe">Для десинка; пусто — решает пресет.</param>
/// <param name="Why">Одной строкой, почему именно это; уходит в журнал.</param>
/// <param name="Optional">
/// Предложение, а не починка: нынешний VPN работает, но можно и без него.
/// Окно не отмечает такие строки галочкой — решает человек.
/// </param>
public sealed record AutoPickChoice(
    AutoPickWay Way, RoutingMode Mode, string? Recipe, string? Title, string Why, bool Optional = false);

/// <summary>Строка автоподбора: одно имя, его нынешний маршрут, пробы и решение.</summary>
/// <remarks>
/// Классом, а не записью: пробы приходят из параллельных замеров и показываются
/// окном по мере поступления, поэтому список закрыт замком и отдаётся снимком.
/// </remarks>
public sealed class AutoPickRow
{
    private readonly List<AutoPickTry> _tries = [];

    public AutoPickRow(AutoPickTarget target, RoutingMode currentMode, string? currentRecipe)
    {
        Target = target;
        Host = Normalize(target.Host);
        CurrentMode = currentMode;
        CurrentRecipe = currentRecipe;
        RuleRecipe = currentRecipe;
    }

    /// <summary>
    /// Рецепт, как он назван в правиле. <see cref="CurrentRecipe"/> перед замером
    /// сводится к имени из выбора по шагам и может стать чужим: 26.10 рецепт
    /// obsidian.md переехал в сервис под именем «Выбрано вручную: valheim».
    /// </summary>
    public string? RuleRecipe { get; }

    /// <summary>
    /// Рецепта из правила нет ни в нынешнем пресете, ни в каталоге — так бывает
    /// после смены пресета: сборка не создаёт профиль, и обход молча пропадает.
    /// </summary>
    public bool RecipeMissing { get; set; }

    public static string Normalize(string host) => host.Trim().TrimStart('*', '.').ToLowerInvariant();

    public AutoPickTarget Target { get; }

    /// <summary>Имя без <c>*.</c>, в нижнем регистре — то, что меряется.</summary>
    public string Host { get; }

    /// <summary>Куда имя идёт сейчас (с учётом выбора движков, как в «Сайт не открывается»).</summary>
    public RoutingMode CurrentMode { get; }

    /// <summary>Рецепт нынешнего правила при десинке; пусто — решает пресет.</summary>
    public string? CurrentRecipe { get; set; }

    /// <summary>Почему не трогали; <c>null</c> — меряли.</summary>
    public string? Skip { get; set; }

    public IReadOnlyList<AutoPickTry> Tries
    {
        get
        {
            lock (_tries)
                return _tries.ToArray();
        }
    }

    public void Add(AutoPickTry attempt)
    {
        lock (_tries)
            _tries.Add(attempt);
    }

    /// <summary>
    /// Стадия пресета отработала для этого имени (winws2 поднялся и имя измерено либо
    /// секции под него нет). Без этого отсутствие пробы пресета ничего не значит.
    /// </summary>
    public bool PresetRan { get; set; }

    /// <summary>
    /// Нынешний маршрут держится на своём правиле <c>*.имя</c>, которое прежде записал
    /// автоподбор, хотя имя принадлежит сервису: дубль его маршрута.
    /// </summary>
    public bool Duplicate { get; set; }

    /// <summary>
    /// Нынешний VPN — заводское правило (базовый набор), а не выбор человека
    /// или автоподбора: уход с него только предлагается.
    /// </summary>
    public bool FactoryVpn { get; set; }

    /// <summary>
    /// Что прибить в hosts при решении <see cref="AutoPickWay.Pin"/>: имя → адрес.
    /// Только имена, чья проба на этом адресе прошла: прочие остались бы без пина.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Pins { get; set; }

    /// <summary>Решение; <c>null</c> — менять нечего.</summary>
    public AutoPickChoice? Choice { get; set; }

    /// <summary>Записано ли решение; <c>null</c> — до записи не дошло.</summary>
    public bool? Written { get; set; }

    /// <summary>Что сказать о записи: «перекрыто правилом …» и т. п.</summary>
    public string? Note { get; set; }
}

/// <summary>Итог прогона: строки, длительность стадий и замечания.</summary>
public sealed record AutoPickReport(
    string Preset,
    IReadOnlyList<AutoPickRow> Rows,
    IReadOnlyDictionary<string, TimeSpan> Timings,
    IReadOnlyList<string> Notes);

/// <summary>Чем кончилась запись одного имени.</summary>
public sealed record AutoPickOutcome(string Host, bool Written, string Note);

/// <summary>
/// Автоподбор маршрутов: для каждого имени пробует все способы и записывает лучший.
/// </summary>
/// <remarks>
/// <para>
/// Способы — от дешёвого к дорогому: напрямую, десинк пресета, каждый рецепт,
/// VPN. Побеждает первый рабочий по этому порядку, среди рецептов — быстрейший.
/// Туннель последним по той же причине, что и в «Сайт не открывается»:
/// он тратит трафик подписки и прибавляет задержку.
/// </para>
/// <para>
/// Меряет честно только при остановленных движках (см. <see cref="RunAsync"/>),
/// а пишет только в пользовательские маршруты, с копией файла и журналом
/// (<see cref="AutoPickJournal"/>), чтобы каждую запись можно было вернуть.
/// </para>
/// </remarks>
public static class AutoPick
{
    /// <summary>Имён, измеряемых одновременно: больше — и замер начинает мерить нашу же очередь.</summary>
    private const int Degree = 4;

    /// <summary>Порт входа для проб VPN; проверка блокировок занимает 24081.</summary>
    private const int VpnPort = 24082;

    /// <summary>
    /// Сколько серверов подписки пробовать: один — выбранный (владелец 25.10).
    /// </summary>
    /// <remarks>
    /// Устойчивость VPN считается по всем его замерам разом, а перепроверка идёт
    /// через первый сервер: с несколькими серверами это пришлось бы вести по каждому.
    /// </remarks>
    private const int VpnServers = 1;

    /// <summary>Услуги с античитом: он плохо переносит и десинк, и туннель, трогать их незачем.</summary>
    private static readonly string[] Anticheat = ["Riot и Valorant", "Battle.net и Blizzard"];

    /// <summary>
    /// Открыло ли имя: страница, перенаправление или ответ 4xx, кроме 451.
    /// </summary>
    /// <remarks>
    /// 403 и 404 на корне — обычный ответ CDN и API: сервер ответил, значит
    /// соединение дошло. 451 — отказ по закону, 5xx — не наш путь, а сервер.
    /// </remarks>
    public static bool Works(int? status) => status is >= 200 and < 500 and not 451;

    /// <summary>
    /// То же для прямой пробы (напрямую, пресет, рецепт), но строже: перенаправление
    /// и 4xx - только при действительном для имени сертификате. Заглушка оператора
    /// или DPI отвечает 302/403, но настоящего сертификата на чужое имя у неё нет.
    /// </summary>
    /// <remarks>
    /// Перенаправление на чужую зону с настоящим сертификатом засчитывается:
    /// youtu.be уводит на youtube.com, и без этого такие имена уходили бы в VPN.
    /// </remarks>
    public static bool Works(RecipeTrial.Flow flow) =>
        flow.Page || (flow.Status is >= 300 and < 500 and not 451 && flow.CertValid);

    /// <summary>
    /// Почему имя не сайт, если ни один способ его не открыл; <c>null</c> — сайт
    /// (или что-то открылось, или пробы не говорят ничего определённого).
    /// </summary>
    /// <remarks>
    /// Проверка берёт одно имя из списка части сервиса, и бывает это голая служебная
    /// зона: discord.media отвечает ошибкой Cloudflare 520 любым путём, у googlevideo.com
    /// сертификат только на поддомены. Блокировкой это не назвать — маршрут тут ни при чём,
    /// а «ничего не помогло» отправляло человека чинить то, что не сломано (25.10).
    /// </remarks>
    public static string? NotASite(AutoPickRow row)
    {
        var all = row.Tries;

        // Пробы пина о самом имени ничего не говорят: адрес мог быть чужим посредником.
        var tries = all.Where(t => t.Way != AutoPickWay.Pin).ToList();

        if (row.Skip is not null || tries.Count == 0 || all.Any(t => t.Works))
            return null;

        // Ошибка самого сервера: до него дошли, страницы у него нет.
        if (tries.FirstOrDefault(t => t.Status is >= 500) is { } error)
            return $"сам адрес отвечает ошибкой {error.Status} — служебное имя, а не страница";

        // Сертификат не на это имя — и напрямую, и через VPN. Одна прямая проба
        // так выглядела бы и у заглушки провайдера, поэтому VPN обязан подтвердить.
        var answered = tries.Where(t => t.Way != AutoPickWay.Vpn && t.Status is not null).ToList();
        var vpn = tries.Where(t => t.Way == AutoPickWay.Vpn).ToList();

        // shortcut: VPN-проба отдаёт отказ сертификата только текстом исключения .NET,
        // при смене формулировки признак пропадёт — тогда завести поле в TunnelReading.
        if (answered.Count > 0 && answered.All(t => t.CertValid == false)
            && vpn.Count > 0 && vpn.All(t => t.Detail.Contains("certificate", StringComparison.OrdinalIgnoreCase)))
        {
            return "сертификат сервера не на это имя — это зона для поддоменов, а не страница";
        }

        // Рукопожатие проходит, а на запрос сервер молча закрывает соединение — на
        // каждом прямом пути и через VPN тоже. Так отвечает узел без HTTPS-страницы:
        // голос, игровой сервер (rtcp.on.epicgames.com, 25.10). DPI режет иначе —
        // тишиной или сбросом, и не через туннель.
        // shortcut: признаки — текст RecipeTrial («сервер закрыл соединение») и .NET
        // («EOF», «0 bytes»); сменится формулировка — завести поля в Flow и TunnelReading.
        var direct = tries.Where(t => t.Way != AutoPickWay.Vpn).ToList();

        if (direct.Count > 0 && direct.All(t => t.Detail.Contains("сервер закрыл соединение", StringComparison.Ordinal))
            && vpn.Count > 0 && vpn.All(t => t.Detail.Contains("EOF", StringComparison.OrdinalIgnoreCase)
                || t.Detail.Contains("0 bytes", StringComparison.OrdinalIgnoreCase)))
        {
            return "сервер принимает соединение, но не отвечает по HTTPS — голос или игровой узел, а не страница";
        }

        return null;
    }

    /// <summary>
    /// Выбирает способ по пробам; <c>null</c> — менять нечего.
    /// </summary>
    /// <remarks>
    /// Работающий нынешний способ не трогаем, даже если есть лучше: подбор чинит
    /// сломанное, а не «улучшает» то, что человек видит рабочим.
    /// </remarks>
    public static AutoPickChoice? Choose(AutoPickRow row)
    {
        if (row.Skip is not null)
            return null;

        var tries = row.Tries;
        int top = tries.Count == 0 ? 0 : tries.Max(t => t.Level);

        if (top == 0)
            return null;

        bool? current = CurrentWorks(row);

        // Работает, но держится на дубле — своём правиле *.имя от прошлого прогона.
        // Переносим тот же способ в маршрут сервиса, дубль при записи снимается.
        if (current == true && row.Duplicate)
            return Move(row);

        // Работающий VPN меняем на путь без него, если тот открывает настоящую
        // страницу (pageOnly: 403 тоже «ответ», но без VPN сайт как раз не работает).
        // Владелец 26.10: «мы должны экономить трафик VPN». Заводской VPN (ChatGPT,
        // Gemini, Claude) — только предложением без галочки: такие сайты отказывают
        // по стране внутри приложения, а проба видит лишь первую страницу.
        if (current == true && row.CurrentMode == RoutingMode.Proxy && top == 2)
        {
            return Best(tries, top, pageOnly: true, pins: row.Pins) is { Way: not AutoPickWay.Vpn } lighter
                ? lighter with { Optional = row.FactoryVpn, Why = "без VPN: " + lighter.Why }
                : null;
        }

        if (current != false)
            return null;

        return Best(tries, top, pins: row.Pins);
    }

    /// <summary>
    /// Работает ли нынешний путь имени: <c>null</c> — его не мерили (стадия
    /// пропущена, winws2 не поднялся), и тогда объявлять его сломанным нельзя.
    /// </summary>
    /// <remarks>
    /// Рабочим считается лучший достигнутый уровень: где-то сайт ответил —
    /// отказ по стране уже не работа (заглушка, 26.10).
    /// </remarks>
    public static bool? CurrentWorks(AutoPickRow row)
    {
        var tries = row.Tries;
        int top = tries.Count == 0 ? 0 : tries.Max(t => t.Level);

        if (top == 0)
            return tries.Count == 0 ? null : false;

        var ok = tries.Where(t => t.Level == top).ToList();

        bool Has(AutoPickWay way) => ok.Any(t => t.Way == way);

        bool Measured(AutoPickWay way) => tries.Any(t => t.Way == way);

        bool SameRecipe(AutoPickTry t) => t.Way == AutoPickWay.Recipe
            && string.Equals(t.Recipe, row.CurrentRecipe, StringComparison.OrdinalIgnoreCase);

        return row.CurrentMode switch
        {
            RoutingMode.Direct => Measured(AutoPickWay.Direct) ? Has(AutoPickWay.Direct) : null,

            // Нет пробы пресета - секция имени не берёт или это pass, и десинк
            // его не трогает: нынешний путь по сути прямой. Но только если стадия
            // пресета прошла: иначе пробы нет просто потому, что не мерили.
            RoutingMode.Desync when string.IsNullOrEmpty(row.CurrentRecipe) =>
                Measured(AutoPickWay.Preset) ? Has(AutoPickWay.Preset)
                : row.PresetRan ? Has(AutoPickWay.Direct)
                : null,

            // Рецепт не находится в нынешнем пресете — маршрут сломан, как его ни мерь.
            RoutingMode.Desync when row.RecipeMissing => false,

            RoutingMode.Desync => tries.Any(SameRecipe) ? ok.Any(SameRecipe) : null,

            _ => Measured(AutoPickWay.Vpn) ? Has(AutoPickWay.Vpn) : null,
        };
    }

    /// <summary>Нынешний способ имени — как запись в маршрут его сервиса.</summary>
    private static AutoPickChoice Move(AutoPickRow row)
    {
        const string why = "перенесено в маршрут сервиса — своё правило на имя было дублем";

        return row.CurrentMode switch
        {
            RoutingMode.Direct => new(AutoPickWay.Direct, RoutingMode.Direct, null, null, why),
            RoutingMode.Proxy => new(AutoPickWay.Vpn, RoutingMode.Proxy, null, null, why),
            _ when string.IsNullOrEmpty(row.CurrentRecipe) =>
                new(AutoPickWay.Preset, RoutingMode.Desync, DesyncRecipes.FromPreset, null, why),
            _ => new(AutoPickWay.Recipe, RoutingMode.Desync, row.RuleRecipe ?? row.CurrentRecipe,
                row.RuleRecipe ?? row.CurrentRecipe, why),
        };
    }

    /// <summary>
    /// Лучший устойчивый способ: напрямую, пресет, быстрейший рецепт, VPN.
    /// </summary>
    /// <remarks>
    /// Устойчивый — сработал во всех замерах. Нынешний маршрут считается рабочим,
    /// если сработал хоть раз (<see cref="Choose"/>), а новый — только если ни разу
    /// не подвёл: 25.10 рецепт rutracker.org открыл сайт за 1,0 с, через полчаса не
    /// дождался, и одного замера хватило, чтобы маршрут переписали.
    /// </remarks>
    private static AutoPickChoice? Best(
        IReadOnlyList<AutoPickTry> tries,
        int level,
        bool pageOnly = false,
        IReadOnlyDictionary<string, string>? pins = null)
    {
        // Предложение уйти с VPN — только по странице или перенаправлению: 403 проверки
        // на робота или 404 на корне ещё не значат, что без туннеля сайт работает.
        bool Good(AutoPickTry t) => t.Level >= level && (!pageOnly || t.Status is >= 200 and < 400);

        List<AutoPickTry>? Stable(IEnumerable<AutoPickTry> samples)
        {
            var list = samples.ToList();
            return list.Count > 0 && list.All(Good) ? list : null;
        }

        if (Stable(tries.Where(t => t.Way == AutoPickWay.Direct)) is not null)
            return new(AutoPickWay.Direct, RoutingMode.Direct, null, null, "открывается напрямую");

        if (Stable(tries.Where(t => t.Way == AutoPickWay.Preset)) is { } preset)
        {
            return new(AutoPickWay.Preset, RoutingMode.Desync, DesyncRecipes.FromPreset, preset[0].Title,
                $"десинк пресета открывает ({preset.Max(t => t.Took).TotalSeconds:0.0} с)");
        }

        // Худший замер рецепта, а не лучший: выбираем тот, что и в плохой раз быстр.
        var recipe = tries
            .Where(t => t.Way == AutoPickWay.Recipe)
            .GroupBy(t => t.Recipe, StringComparer.OrdinalIgnoreCase)
            .Select(g => Stable(g))
            .Where(g => g is not null)
            .OrderBy(g => g!.Max(t => t.Took))
            .FirstOrDefault();

        if (recipe is not null)
        {
            var slow = recipe.Max(t => t.Took);

            return new(AutoPickWay.Recipe, RoutingMode.Desync, recipe[0].Recipe, recipe[0].Title,
                $"рецепт «{recipe[0].Title}» — {slow.TotalSeconds:0.0} с");
        }

        // Пин дешевле VPN: ни трафика подписки, ни задержки. Прибиты только пробованные
        // имена — прочие имена сайта пойдут как шли, и об этом сказано в причине.
        if (Stable(tries.Where(t => t.Way == AutoPickWay.Pin)) is { } pin)
        {
            var names = pins is { Count: > 0 } ? "; прибиты: " + string.Join(", ", pins.Keys) : "";

            return new(AutoPickWay.Pin, RoutingMode.Direct, null, pin[0].Title,
                $"прибито в hosts к {pin[0].Address} ({pin[0].Title}) — открывается без VPN{names}");
        }

        if (Stable(tries.Where(t => t.Way == AutoPickWay.Vpn)) is { } vpn)
        {
            // Без VPN сервер отвечал, но отказом: это не блокировка, а страна.
            bool refused = tries.Any(t => t.Way != AutoPickWay.Vpn && t.Level == 1);

            return new(AutoPickWay.Vpn, RoutingMode.Proxy, null, null, refused
                ? $"без VPN сайт отказывает по стране, через VPN открывается ({vpn[0].Server})"
                : $"открывается через VPN ({vpn[0].Server})");
        }

        return null;
    }

    /// <summary>
    /// Какие способы этой строки перемерить, прежде чем менять маршрут.
    /// </summary>
    /// <remarks>
    /// Два вопроса — две пробы. Правда ли сломан нынешний способ (одна неудача
    /// бывает и у рабочего), и правда ли держится выбранный. Перемеряется то,
    /// у чего замер пока один.
    /// </remarks>
    public static IReadOnlyList<(AutoPickWay Way, string? Recipe)> NeedsRepeat(AutoPickRow row)
    {
        if (row.Choice is not { } choice)
            return [];

        var tries = row.Tries;
        var repeat = new List<(AutoPickWay, string?)>();

        int Samples(AutoPickWay way, string? recipe) => tries.Count(t => t.Way == way
            && (way != AutoPickWay.Recipe || string.Equals(t.Recipe, recipe, StringComparison.OrdinalIgnoreCase)));

        // Нынешний способ — только если его признали сломанным (при уходе с рабочего
        // VPN и при переносе дубля он работает, перемерять нечего).
        if (CurrentWorks(row) == false)
        {
            var current = row.CurrentMode switch
            {
                RoutingMode.Direct => (AutoPickWay.Direct, (string?)null),
                RoutingMode.Proxy => (AutoPickWay.Vpn, null),
                _ when string.IsNullOrEmpty(row.CurrentRecipe) => (row.Tries.Any(t => t.Way == AutoPickWay.Preset)
                    ? AutoPickWay.Preset : AutoPickWay.Direct, null),
                _ => (AutoPickWay.Recipe, row.CurrentRecipe),
            };

            if (Samples(current.Item1, current.Item2) == 1)
                repeat.Add(current);
        }

        var chosen = (choice.Way, choice.Way == AutoPickWay.Recipe ? choice.Recipe : null);

        if (Samples(chosen.Item1, chosen.Item2) == 1 && !repeat.Contains(chosen))
            repeat.Add(chosen);

        return repeat;
    }

    /// <summary>
    /// Прогоняет все стадии и возвращает пробы с решениями; на диск ничего не пишет.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Движки работают: работающий десинк правил бы те же пакеты вторым слоем,
    /// а туннель открыл бы имя любым способом — мерить было бы нечего.
    /// </exception>
    public static async Task<AutoPickReport> RunAsync(
        IReadOnlyList<AutoPickTarget> targets,
        IReadOnlyList<ProxyServer> vpnServers,
        IProgress<AutoPickRow>? progress,
        CancellationToken cancellationToken)
    {
        var state = SupervisorState.Load(SupervisorState.DefaultPath);

        if (state is not null && state.IsSupervisorAlive())
            throw new InvalidOperationException("Сначала остановите движки на «Главной»: подбор меряет голую сеть.");

        var strangers = Process.GetProcessesByName("winws2");
        bool stranger = strangers.Length > 0;

        foreach (var each in strangers)
            each.Dispose();

        if (stranger)
            throw new InvalidOperationException("Запущен посторонний winws2 — он исказил бы замеры. Закройте его и повторите.");

        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var (engine, _) = RuleSetExpander.LoadFor(settings);
        var notes = new List<string>();
        var timings = new Dictionary<string, TimeSpan>();

        var paths = ZapretPaths.Discover();
        var winws = paths is null ? null : Path.Combine(paths.Root, "exe", "winws2.exe");
        ZapretPreset? preset = null;

        try
        {
            if (settings.PresetName is { } presetName && ZapretPaths.FindPreset(presetName) is { } presetFile)
                preset = new PresetReader().Load(presetFile);
        }
        catch (Exception ex)
        {
            notes.Add("пресет не прочитался: " + ex.GetBaseException().Message);
        }

        bool canDesync = paths is not null && winws is not null && File.Exists(winws) && preset is not null;

        if (!canDesync)
            notes.Add("winws2 или пресет не найдены — пробы десинка пропущены");

        var pinned = HostsFile.Read();
        var ownPins = HostsEditor.Pins();
        var addresses = new ConcurrentDictionary<AutoPickRow, IPAddress>();
        var rows = new List<AutoPickRow>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Свои правила *.имя, записанные прежними прогонами и не снятые: у имени
        // сервиса это дубль его маршрута, и при записи он переедет в сервис.
        // Последняя запись на значение — та, что должна стоять в файле сейчас.
        var ours = new Dictionary<string, UserRuleEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in AutoPickJournal.Load().SelectMany(r => r.Entries)
                     .Where(e => !e.Restored && e.Wrote is { Match: MatchKind.Domain } && e.Pins is null))
        {
            ours[entry.Value] = entry.Wrote!;
        }

        var userRules = UserRulesFile.Load().Entries;

        // Дубль — только правило ровно на это имя и ровно в том виде, как его записал
        // автоподбор: правило на родительскую зону или поправленное руками — выбор
        // человека, переносить его в сервис нельзя.
        bool IsDuplicate(AutoPickTarget target)
        {
            if (target.List is null)
                return false;

            var value = SiteFixer.RuleValue(AutoPickRow.Normalize(target.Host));

            return ours.TryGetValue(value, out var wrote)
                && userRules.Any(e => e.Match == MatchKind.Domain && e.Matches(value) && e == wrote);
        }

        foreach (var item in targets)
        {
            var target = item;

            // Одно правило - одна строка: иначе пробы VPN и запись шли бы дважды,
            // а www.x и x пишутся в одно и то же *.x - и вторая затёрла бы первую.
            if (!seen.Add(SiteFixer.RuleValue(AutoPickRow.Normalize(target.Host))))
                continue;

            var exact = SiteFixer.RuleValue(AutoPickRow.Normalize(target.Host));

            // Своё правило ровно на это имя (поставленное в «Маршрутах» или «Сайт не
            // открывается») бьёт маршрут сервиса. Писать в сервис бесполезно — проверка
            // после записи сняла бы запись как перекрытую; пишем туда, где решает человек.
            if (target.List is not null && !IsDuplicate(target)
                && userRules.Any(e => e.Match == MatchKind.Domain && e.Enabled && !e.Off && e.Matches(exact)))
            {
                target = target with { List = null, Within = null };
            }

            // Часть, выключенная человеком или заводской настройкой, — его решение:
            // подбор её не включает (аудит 26.10: Off и Enabled=false молча затирались).
            var part = target.List is null ? null
                : userRules.FirstOrDefault(e => e.Match == MatchKind.HostList && e.Matches(target.List));

            string? off = part is { Off: true } or { Enabled: false } ? "часть выключена в «Маршрутах»"
                : part is null && target.DefaultOff ? "часть выключена по умолчанию"
                : null;

            var decision = engine.Evaluate(ConnectionEvent.Describe(AutoPickRow.Normalize(target.Host)));
            var mode = settings.Engines.Effective(decision.Mode, settings.NeedsProxy);

            var row = new AutoPickRow(target, mode, mode == RoutingMode.Desync ? decision.Rule?.Recipe : null)
            {
                FactoryVpn = mode == RoutingMode.Proxy && decision.Rule is { Source: RuleSource.Base },
                RecipeMissing = mode == RoutingMode.Desync && preset is not null
                    && !string.IsNullOrEmpty(decision.Rule?.Recipe)
                    && !RecipeResolver.Known(preset, decision.Rule!.Recipe),
                Duplicate = IsDuplicate(target)
                    && decision.Rule is { Match: MatchKind.Domain } rule
                    && string.Equals(rule.Value, SiteFixer.RuleValue(AutoPickRow.Normalize(target.Host)),
                        StringComparison.OrdinalIgnoreCase),
            };

            if (off is not null)
                row.Skip = off;
            else if (Anticheat.Any(a => target.Label.StartsWith(a, StringComparison.OrdinalIgnoreCase)))
                row.Skip = "античит плохо переносит и десинк, и туннель";
            else if (pinned.ContainsKey(row.Host))
                row.Skip = ownPins.ContainsKey(row.Host) ? null : "имя прибито в hosts";

            rows.Add(row);
        }

        // Наш пин стоит на имени — работает ли он ещё: от этого зависит, что посоветовать.
        // Чужую строку не мерим и не трогаем.
        await Each(rows.Where(r => r.Skip is null && ownPins.ContainsKey(r.Host)).ToList(), async row =>
        {
            var probe = await PinPicker.ProbeAsync(
                new PinCandidate(ownPins[row.Host], PinSource.Pool, "прибито"), row.Host, cancellationToken);

            row.Skip = PinWorks(probe)
                ? "прибито в hosts — пин работает"
                : $"прибито в hosts — пин не отвечает: {probe.Detail}; переподберите в «Файл hosts» или снимите";
        }, cancellationToken);

        // Адреса: у самого имени, у его предка по зоне, наконец — у DoH. Имя,
        // которого нет нигде (маркер зоны, голосовой узел под звонок), мерить нечем.
        //
        // DoH спрашивается всегда и бьёт системный ответ: подделанный DNS отдаёт
        // чужой адрес, и все пробы мерили бы его, а не путь до имени.
        using var dohClient = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };

        await Stage("dns", timings, () => Each(rows.Where(r => r.Skip is null), async row =>
        {
            var address = await RecipeTrial.AddressForAsync(row.Host, cancellationToken);

            if (await DohResolver.ResolveAsync(row.Host, dohClient, cancellationToken) is { } doh
                && IPAddress.TryParse(doh, out var parsed)
                && !HostsFile.IsBlocking(parsed))
            {
                address = parsed;
            }

            // Резолверы отмену глотают и отвечают «нет адреса»: это не исход имени.
            cancellationToken.ThrowIfCancellationRequested();

            if (address is null || HostsFile.IsBlocking(address))
                row.Skip = "имя не разрешается";
            else
                addresses[row] = address;

            progress?.Report(row);
        }, cancellationToken));

        var live = rows.Where(r => r.Skip is null).ToList();

        async Task Measure(AutoPickRow row, AutoPickWay way, string? recipe, string? title)
        {
            var clock = Stopwatch.StartNew();
            var flow = await RecipeTrial.OpensAsync(row.Host, addresses[row], cancellationToken);
            clock.Stop();

            row.Add(new AutoPickTry(way, recipe, title, null, Works(flow), clock.Elapsed,
                RecipeTrial.Verdict(flow, clock.Elapsed), flow.Status, flow.Status is null ? null : flow.CertValid,
                flow.Stub));

            progress?.Report(row);
        }

        // Пресет — с теми же настройками страницы «Десинк», что в работе (SupervisorHost):
        // game filter и ширина перехвата меняют, какие соединения он берёт, и замер
        // без них мерил бы не то, что потом запустится (вопрос владельца 26.10).
        // Щит, свои профили и туннель не передаются: их и выбирает подбор.
        IReadOnlyList<string> PresetArguments() =>
            WinwsCommandLine.Build(preset!, gameFilter: settings.GameFilter, capture: settings.Capture);

        // a) Напрямую: движков нет, мерим голую сеть.
        await Stage("direct", timings, () =>
            Each(live, row => Measure(row, AutoPickWay.Direct, null, null), cancellationToken));

        // b) Пресет целиком, одним winws2, — как он работал бы в жизни.
        if (canDesync)
        {
            await Stage("preset", timings, async () =>
            {
                try
                {
                    var zones = PresetZones.Build(preset!, paths!.Root);

                    // Все имена, а не только те, что секция берёт по имени. Пресет ловит
                    // ещё и по адресам (ipset): 26.10 telegram.org, notion.com, netflix.com
                    // так и работали, а без пробы пресета подбор судил их по прямой —
                    // и уводил в VPN то, что открывалось десинком.
                    var covered = live
                        .Select(r => (Row: r, Title: zones.MatchFor(r.Host)?.Name ?? "по адресам или без секции"))
                        .ToList();

                    var failed = await RecipeTrial.WithWinwsAsync(
                        winws!,
                        paths.Root,
                        PresetArguments(),
                        () => Each(covered, x => Measure(x.Row, AutoPickWay.Preset, null, x.Title),
                            cancellationToken),
                        cancellationToken);

                    if (failed is not null)
                        notes.Add("пресет: " + failed);
                    else
                        live.ForEach(r => r.PresetRan = true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    notes.Add("пресет: " + ex.GetBaseException().Message);
                }
            });
        }

        var singBox = settings.NeedsProxy && vpnServers.Count > 0 ? TunnelStatus.FindSingBox() : null;

        // Проба VPN пачкой имён через один сервер. Своя папка и свой порт на каждый
        // запуск: sing-box прошлого может ещё не отпустить reach.log и порт, и общая
        // папка роняла весь прогон с «файл занят другим процессом».
        int vpnRuns = 0;

        async Task<List<AutoPickRow>> MeasureVpn(ProxyServer server, IReadOnlyList<AutoPickRow> batch)
        {
            int run = vpnRuns++;
            var work = Path.GetFullPath(Path.Combine("runtime", "autopick", $"vpn-{run}"));

            var readings = await TunnelReach.CheckAsync(
                singBox!, server, batch.Select(r => r.Host).ToList(), work, VpnPort + run, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            var reached = new List<AutoPickRow>();

            foreach (var reading in readings)
            {
                if (batch.FirstOrDefault(r => r.Host == reading.Host) is not { } row)
                    continue;

                bool works = reading.Reached && Works(reading.Status);

                // 403 через VPN — тоже отказ: проба туннеля видит только код, ни заголовка
                // проверки на робота, ни тела. Иначе 403 напрямую (отказ) проигрывал бы
                // тем же 403 через VPN (medium.com, 26.10).
                row.Add(new AutoPickTry(AutoPickWay.Vpn, null, null, server.Tag, works, reading.Elapsed,
                    reading.Detail ?? (reading.Status is { } code ? $"ответ {code}" : "нет ответа"),
                    reading.Status, Stub: reading.Status is 403 or 451));

                if (works)
                    reached.Add(row);

                progress?.Report(row);
            }

            return reached;
        }

        // c0) Сперва — не служебное ли это имя. Владелец 26.10: «не сайт» проверялся
        // всеми рецептами подряд, и на это уходило время. Признаки видны уже после
        // прямой пробы и пресета: ошибка самого сервера, молчаливое закрытие после
        // рукопожатия, чужой сертификат. Таким — сразу VPN: подтвердит, что сервер
        // и там ведёт себя так же, — рецепты им не нужны.
        if (singBox is not null)
        {
            var suspects = live
                .Where(r => !r.Tries.Any(t => t.Works) && r.Tries.Any(t => t.Way != AutoPickWay.Vpn
                    && (t.Status >= 500
                        || t.Detail.Contains("сервер закрыл соединение", StringComparison.Ordinal)
                        || t.Status is not null && t.CertValid == false)))
                .ToList();

            if (suspects.Count > 0)
                await Stage("not-site", timings, () => MeasureVpn(vpnServers[0], suspects));
        }

        // Понадобятся и перепроверке: она поднимает те же рецепты заново.
        IReadOnlyList<RecipeChoice> choices = [];
        var list = Path.GetFullPath(Path.Combine(WinwsCommandLine.OwnListsDirectory, "autopick.txt"));

        // c) Каждый рецепт отдельным winws2 — для имён, которые ещё не открылись.
        if (canDesync)
        {
            await Stage("recipes", timings, async () =>
            {
                // Не сайт — рецептам мерить нечего. Ответил хоть отказом — тоже: рецепт
                // чинит только прохождение DPI, а отказ по стране меняет лишь VPN.
                var open = live
                    .Where(r => NotASite(r) is null && !r.Tries.Any(t => t.Works
                        && t.Way is AutoPickWay.Direct or AutoPickWay.Preset))
                    .ToList();

                // Рецепт, который стоит сейчас, мерим всегда - даже если имя уже открылось
                // напрямую: без его пробы не узнать, сломан он или нет.
                var current = live.Where(r => !open.Contains(r) && !string.IsNullOrEmpty(r.CurrentRecipe)).ToList();

                if (open.Count == 0 && current.Count == 0)
                    return;

                IReadOnlyDictionary<string, string> providers = new Dictionary<string, string>();

                try
                {
                    providers = LuaModules.Providers(LuaModules.Scan(paths!.LuaDirectory));
                }
                catch (Exception)
                {
                    // Без приёмов останутся наборы пресета — сужение выбора, а не отказ.
                }

                // Рецепт пресета записывается именем его секции («Выбрано вручную: valheim»),
                // и в другом пресете такой секции нет: рецепт перестаёт находиться, а маршрут
                // молча теряет обход. Если в каталоге есть тот же набор шагов — пишем его Id:
                // каталог от пресета не зависит (вопрос владельца о смене пресета, 26.10).
                choices = RecipeChoices.Build(preset!, providers, null)
                    .Select(c => RecipeCatalog.All.FirstOrDefault(r => r.Steps.SequenceEqual(c.Steps)) is { } same
                        ? c with { Name = same.Id }
                        : c)
                    .ToList();

                // Сохранённый рецепт мог быть назван иначе, чем его вариант в выборе
                // (повторы сводятся по шагам): сопоставляем по шагам, иначе нынешний
                // способ не нашёл бы своей пробы и казался бы сломанным.
                foreach (var row in live.Where(r => !string.IsNullOrEmpty(r.CurrentRecipe)))
                {
                    if (choices.Any(c => string.Equals(c.Name, row.CurrentRecipe, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var steps = RecipeResolver.Find(preset!, row.CurrentRecipe, providers).Steps;

                    if (steps.Count > 0 && choices.FirstOrDefault(c => c.Steps.SequenceEqual(steps)) is { } same)
                        row.CurrentRecipe = same.Name;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(list)!);
                var hosts = open.Concat(current).Select(r => r.Host).ToList();
                await File.WriteAllLinesAsync(list, hosts, cancellationToken);

                foreach (var choice in choices.Where(c => c.Steps.Count > 0))
                {
                    var failed = await RecipeTrial.WithWinwsAsync(
                        winws!,
                        paths!.Root,
                        RecipeTrial.RecipeArguments(preset!, paths.Root, hosts, choice.Steps, list),
                        () => Each(
                            open.Concat(current.Where(r => string.Equals(
                                r.CurrentRecipe, choice.Name, StringComparison.OrdinalIgnoreCase))),
                            row => Measure(row, AutoPickWay.Recipe, choice.Name, choice.Title),
                            cancellationToken),
                        cancellationToken);

                    // Не поднявшийся рецепт — недоступный, а не «не помогает».
                    if (failed is not null)
                        notes.Add($"рецепт «{choice.Title}»: {failed}");
                }
            });
        }

        // c2) Пин в hosts: имя прибивается к посреднику или настоящему адресу. Дешевле
        // VPN, а не наоборот — потому стоит перед ним; нужен тем, у кого ни один из
        // способов выше страницы не открыл.
        await Stage("pin", timings, async () =>
        {
            // Не для тех, кто сейчас в VPN. Пин покрывает одно-два имени, а маршрут
            // сервиса — весь список: снять с него VPN ради пина значило бы оставить
            // без туннеля и его CDN и API. И рабочий VPN не меняем на посредника,
            // который живёт, пока живёт его узел (ревью 26.10).
            var wanted = live
                .Where(r => r.CurrentMode != RoutingMode.Proxy && NotASite(r) is null
                    && !r.Tries.Any(t => t.Level == 2 && t.Way != AutoPickWay.Vpn))
                .ToList();

            if (wanted.Count == 0)
                return;

            var (known, pool) = await PinSources.LoadAsync(
                wanted.Select(r => r.Host), ZapretCatalog.Discover(paths?.Root), OwnCatalog.Load(), cancellationToken);

            await Each(wanted, async row =>
            {
                // Голое имя и www: сайт часто переадресует с одного на другое, и без
                // второго пин получил бы 1009 уже на переадресации (PinPicker.PickAsync).
                List<string> names = [row.Host];

                if (row.Host.Count(c => c == '.') == 1)
                    names.Add("www." + row.Host);

                var picks = await PinPicker.PickAsync(names, known, pool, null, cancellationToken, null, row.Host);

                Pinned(row, picks);
                progress?.Report(row);
            }, cancellationToken, degree: 2);
        });

        // d) VPN: тем, у кого ещё нет страницы (не открылись или открылись только
        // отказом — заглушка страны), и тем, что идут в туннель сейчас.
        if (singBox is not null)
        {
            await Stage("vpn", timings, async () =>
            {
                var left = live
                    .Where(r => NotASite(r) is null && !r.Tries.Any(t => t.Way == AutoPickWay.Vpn))
                    .Where(r => r.CurrentMode == RoutingMode.Proxy || r.Tries.All(t => t.Level < 2))
                    .ToList();

                foreach (var server in vpnServers.Take(VpnServers))
                {
                    if (left.Count == 0)
                        break;

                    foreach (var row in await MeasureVpn(server, left))
                        left.Remove(row);
                }
            });
        }
        else if (settings.NeedsProxy && vpnServers.Count > 0)
        {
            notes.Add("sing-box не найден — пробы VPN пропущены");
        }

        // Отменённый прогон не должен вернуть неполный отчёт как законченный.
        cancellationToken.ThrowIfCancellationRequested();

        // e) Перепроверка: прежде чем менять маршрут, мерим ещё раз и нынешний
        // способ, и выбранный. Не устоял выбранный — берём следующий и мерим его;
        // три круга хватает, дальше пусть остаётся как есть.
        await Stage("confirm", timings, async () =>
        {
            for (int round = 0; round < 3; round++)
            {
                foreach (var row in rows)
                    row.Choice = Choose(row);

                var plan = rows
                    .SelectMany(r => NeedsRepeat(r).Select(k => (Row: r, k.Way, k.Recipe)))
                    .ToList();

                if (plan.Count == 0)
                    break;

                await Each(plan.Where(p => p.Way == AutoPickWay.Direct),
                    p => Measure(p.Row, AutoPickWay.Direct, null, null), cancellationToken);

                if (canDesync && plan.Where(p => p.Way == AutoPickWay.Preset).ToList() is { Count: > 0 } presets)
                {
                    var failed = await RecipeTrial.WithWinwsAsync(winws!, paths!.Root, PresetArguments(),
                        () => Each(presets, p => Measure(p.Row, AutoPickWay.Preset, null,
                            p.Row.Tries.First(t => t.Way == AutoPickWay.Preset).Title), cancellationToken),
                        cancellationToken);

                    if (failed is not null)
                        notes.Add("перепроверка пресета: " + failed);
                }

                foreach (var group in plan.Where(p => p.Way == AutoPickWay.Recipe)
                    .GroupBy(p => p.Recipe, StringComparer.OrdinalIgnoreCase))
                {
                    if (!canDesync || choices.FirstOrDefault(c =>
                            string.Equals(c.Name, group.Key, StringComparison.OrdinalIgnoreCase)) is not { } choice)
                    {
                        continue;
                    }

                    var hosts = group.Select(p => p.Row.Host).ToList();
                    await File.WriteAllLinesAsync(list, hosts, cancellationToken);

                    var failed = await RecipeTrial.WithWinwsAsync(winws!, paths!.Root,
                        RecipeTrial.RecipeArguments(preset!, paths.Root, hosts, choice.Steps, list),
                        () => Each(group, p => Measure(p.Row, AutoPickWay.Recipe, choice.Name, choice.Title),
                            cancellationToken),
                        cancellationToken);

                    if (failed is not null)
                        notes.Add($"перепроверка рецепта «{choice.Title}»: {failed}");
                }

                await Each(plan.Where(p => p.Way == AutoPickWay.Pin), async p =>
                {
                    var was = p.Row.Tries.Last(t => t.Way == AutoPickWay.Pin);

                    p.Row.Add(PinTry(await PinPicker.ProbeAsync(
                        new PinCandidate(was.Address!, PinSource.Pool, was.Title ?? ""), p.Row.Host, cancellationToken)));
                    progress?.Report(p.Row);
                }, cancellationToken);

                if (singBox is not null && plan.Where(p => p.Way == AutoPickWay.Vpn).Select(p => p.Row).ToList()
                        is { Count: > 0 } viaVpn)
                {
                    await MeasureVpn(vpnServers[0], viaVpn);
                }
            }

            // Итог — по всем замерам. Решение, не подтверждённое вторым (способ не
            // перемерился: winws2 не поднялся, рецепта нет в выборе), не пишем.
            foreach (var row in rows)
            {
                row.Choice = Choose(row);

                if (row.Choice is not null && NeedsRepeat(row).Any(k =>
                        k.Way == row.Choice.Way && (k.Way != AutoPickWay.Recipe
                            || string.Equals(k.Recipe, row.Choice.Recipe, StringComparison.OrdinalIgnoreCase))))
                {
                    row.Choice = null;
                }
            }
        });

        foreach (var row in rows)
            progress?.Report(row);

        return new AutoPickReport(preset?.Name ?? settings.PresetName ?? "", rows, timings, notes);
    }

    /// <summary>
    /// Годится ли проба для пина: сайт ответил сам, не отказом и не проверкой на робота,
    /// и адрес не настоящий.
    /// </summary>
    /// <remarks>
    /// Проверка на робота отсюда не видна: за ней может быть и сайт, и отказ по стране.
    /// Настоящий адрес прибивать нечего: он и так разрешается так же, а раз прямая проба
    /// не открыла, то и пин на него не поможет.
    /// </remarks>
    public static bool PinWorks(PinProbe probe) =>
        probe.Verdict == PinVerdict.Works && Works(probe.Status) && probe.Candidate.Source != PinSource.Honest;

    /// <summary>Проба пина как проба автоподбора.</summary>
    public static AutoPickTry PinTry(PinProbe probe)
    {
        bool works = PinWorks(probe);

        var detail = probe.Detail
            + (probe.Verdict == PinVerdict.Challenge && !probe.ExitsInRussia ? " — можно прибить вручную в «Файл hosts»" : "")
            + (probe.Verdict == PinVerdict.Works && probe.Candidate.Source == PinSource.Honest
                ? " — настоящий адрес, пин не поможет" : "");

        return new AutoPickTry(AutoPickWay.Pin, null, probe.Candidate.Label, null, works, probe.Elapsed, detail,
            probe.Status, null, false, probe.Candidate.Address);
    }

    /// <summary>Заносит исход подбора адреса: пробу пина главного имени и имена, что прибьются.</summary>
    private static void Pinned(AutoPickRow row, IReadOnlyList<PinPick> picks)
    {
        if (picks.FirstOrDefault(p => string.Equals(p.Host, row.Host, StringComparison.OrdinalIgnoreCase)) is not { } main)
            return;

        IEnumerable<PinProbe> All(PinPick pick) =>
            pick.Chosen is { } chosen ? pick.Rejected.Prepend(chosen) : pick.Rejected;

        // Выбранный подбором бывает настоящим адресом (он один на все имена, и сайт
        // «целиком без посредника»), а нам он не годится: берём лучший рабочий посредник.
        var best = main.Chosen is { } c && PinWorks(c) ? c : All(main).FirstOrDefault(PinWorks);

        if (best is null)
        {
            // Сколько адресов перебрали — иначе в журнале стоял один «честный резолвер:
            // нет ответа», будто пробовали только его (netflix.com, 26.10).
            if ((main.Chosen ?? main.Rejected.FirstOrDefault()) is { } failed)
            {
                int tried = All(main).Count();

                row.Add(PinTry(failed) with
                {
                    Detail = $"перебрано адресов: {tried}, ни один не открыл (лучший: {failed.Detail})",
                });
            }

            return;
        }

        var address = best.Candidate.Address;

        var pins = picks
            .Where(p => All(p).Any(x => PinWorks(x) && x.Candidate.Address == address))
            .ToDictionary(p => p.Host, _ => address, StringComparer.OrdinalIgnoreCase);

        // Сайт уводит на www (или другое имя той же зоны), а его этот адрес не открыл:
        // после пина браузер ушёл бы на неприбитое имя и получил прежний отказ —
        // ровно то, от чего PinPicker и подбирает имена перенаправлений.
        if (best.Redirect is { } next && !pins.ContainsKey(next))
        {
            row.Add(PinTry(best) with
            {
                Works = false,
                Detail = $"уводит на {next}, а его этот адрес не открыл — пин не поможет",
            });

            return;
        }

        row.Add(PinTry(best));
        row.Pins = pins;
    }

    private static async Task Stage(string name, Dictionary<string, TimeSpan> timings, Func<Task> work)
    {
        var clock = Stopwatch.StartNew();

        try
        {
            await work();
        }
        finally
        {
            timings[name] = clock.Elapsed;
        }
    }

    private static Task Each<T>(
        IEnumerable<T> items,
        Func<T, Task> body,
        CancellationToken cancellationToken,
        int degree = Degree) =>
        Parallel.ForEachAsync(
            items,
            new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cancellationToken },
            async (item, _) => await body(item));

    /// <summary>
    /// Журнал прогона текстом: по каждому имени — что пробовали, что ответило и что решено.
    /// </summary>
    /// <remarks>
    /// Владелец 25.10 спросил, почему не всё открылось, — а ответ остался только
    /// в окне: журнал откатов хранит одни записанные имена, и «не помогло»
    /// разбиралось по логам sing-box и ручным пробам.
    /// </remarks>
    public static string Describe(AutoPickReport report, DateTimeOffset at)
    {
        var text = new System.Text.StringBuilder();

        text.AppendLine($"Автоподбор маршрутов — {at:dd.MM.yyyy HH:mm}");
        text.AppendLine(new string('=', 78));
        text.AppendLine($"Пресет: {report.Preset}");
        text.AppendLine("Этапы: " + string.Join(", ", report.Timings.Select(t => $"{t.Key} {t.Value:m\\:ss}")));

        foreach (var note in report.Notes)
            text.AppendLine("Замечание: " + note);

        text.AppendLine();

        foreach (var row in report.Rows)
        {
            text.AppendLine($"{row.Host} — {row.Target.Label}");

            var current = row.CurrentMode switch
            {
                RoutingMode.Direct => "напрямую",
                RoutingMode.Proxy => "через VPN",
                _ => string.IsNullOrEmpty(row.CurrentRecipe) ? "десинк: пресет" : "десинк: " + (row.RuleRecipe ?? row.CurrentRecipe) + (row.RecipeMissing ? " — нет ни в пресете, ни в каталоге" : ""),
            };

            text.AppendLine("  сейчас: " + current);

            foreach (var t in row.Tries)
            {
                var way = t.Way switch
                {
                    AutoPickWay.Direct => "напрямую",
                    AutoPickWay.Preset => $"пресет «{t.Title}»",
                    AutoPickWay.Recipe => $"рецепт «{t.Title}»",
                    AutoPickWay.Pin => $"пин {t.Address} ({t.Title})",
                    _ => $"VPN «{t.Server}»",
                };

                text.AppendLine($"  {(t.Works ? "+" : "-")} {way}: {t.Detail}");
            }

            var verdict = row.Skip is { } skip ? "пропущено: " + skip
                : row.Choice is { Optional: true } offer ? "предложено (без галочки): " + offer.Why
                : row.Choice is { } choice ? "записать: " + choice.Why
                : NotASite(row) is { } notSite ? "не сайт: " + notSite
                : row.Tries.Count == 0 ? "не измерено"
                : CurrentWorks(row) switch
                {
                    true when row.Tries.Max(t => t.Level) == 1 =>
                        "оставить как есть: сайт отказывает по стране на любом пути — ни VPN, ни пин не помогли",
                    true => "оставить как есть: нынешний путь работает",
                    null => "оставить как есть: нынешний путь не измерен",

                    // 26.10 netflix.com стоял «нынешний путь работает» при молчащем
                    // пресете: рабочего не нашлось, а не нынешний исправен.
                    _ => row.Tries.Any(t => t.Works)
                        ? "ничего устойчивого: что-то открывалось через раз или только заглушкой — оставлено как есть"
                        : "ничего не помогло",
                };

            text.AppendLine("  итог: " + verdict);
            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>Кладёт журнал прогона в <c>reports\autopick-…txt</c>, рядом с отчётами проверки.</summary>
    /// <returns>Полный путь файла.</returns>
    public static string SaveLog(AutoPickReport report, string directory = "reports")
    {
        var at = DateTimeOffset.Now;
        // С секундами: два прогона в одну минуту иначе писали бы в один файл.
        var path = Path.GetFullPath(Path.Combine(directory, $"autopick-{at:yyyy-MM-dd-HHmmss}.txt"));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Без метки порядка байтов — как отчёт проверки: его читают и Windows, и веб-формы.
        File.WriteAllText(path, Describe(report, at), new System.Text.UTF8Encoding(false));

        // Последние двадцать, как у отчётов проверки (CheckView.KeepReports): иначе
        // журналы копились бы без предела.
        try
        {
            foreach (var old in new DirectoryInfo(Path.GetDirectoryName(path)!).GetFiles("autopick-*.txt")
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(20))
            {
                old.Delete();
            }
        }
        catch (IOException)
        {
            // Занятый старый журнал удалится в следующий раз.
        }

        return path;
    }

    /// <summary>
    /// Записывает решения в свои маршруты, сверяет и заносит в журнал.
    /// </summary>
    /// <param name="rulesPath">Файл своих маршрутов; по умолчанию рабочий.</param>
    /// <param name="evaluate">
    /// Как узнать, что вышло из записи: режим, рецепт и правило, которое победило.
    /// По умолчанию — движок правил на рабочем файле; для чужого файла проверка
    /// без этого параметра не делается (движок читает только рабочий).
    /// </param>
    /// <param name="hostsPath">
    /// Файл hosts для пинов; <c>null</c> — настоящий, со сбросом кэша DNS. Не <c>null</c> —
    /// для тестов: сброс не делается.
    /// </param>
    /// <remarks>
    /// <para>
    /// Перед записью файл копируется в <c>rules.user.before-autopick.yaml</c>.
    /// Новая запись встаёт перед первым своим правилом на более широкую зону,
    /// иначе оно перехватило бы имя раньше (движок берёт первое совпавшее).
    /// </para>
    /// <para>
    /// После записи имя заново прогоняется через движок: если победило другое
    /// правило (базовое «выше» своих, выключатель режима), запись снимается —
    /// писать то, что не действует, значит обманывать человека.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<AutoPickOutcome> Apply(
        AutoPickReport report,
        string? rulesPath = null,
        string? journalPath = null,
        Func<string, (RoutingMode Mode, string? Recipe, string? Rule)>? evaluate = null,
        string? hostsPath = null)
    {
        var path = rulesPath ?? UserRulesFile.DefaultPath;
        var snapshot = File.Exists(path) ? File.ReadAllBytes(path) : null;

        try
        {
            return ApplyCore(report, rulesPath, journalPath, evaluate, hostsPath);
        }
        catch (Exception original)
        {
            // Сбой между записью файла и журналом оставил бы правки без пути назад:
            // возвращаем файл, каким он был, и отдаём ошибку.
            try
            {
                if (snapshot is not null)
                    UserRulesFile.WriteAtomic(path, snapshot);
                else if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception rollback)
            {
                // Сбой отката не должен скрыть исходную ошибку: человеку нужны обе.
                throw new AggregateException(
                    "Запись не удалась, и файл маршрутов вернуть не вышло — прежний лежит в "
                    + Path.Combine(Path.GetDirectoryName(path) ?? "", "rules.user.before-autopick.yaml"),
                    original, rollback);
            }

            throw;
        }
    }

    private static IReadOnlyList<AutoPickOutcome> ApplyCore(
        AutoPickReport report,
        string? rulesPath,
        string? journalPath,
        Func<string, (RoutingMode Mode, string? Recipe, string? Rule)>? evaluate,
        string? hostsPath)
    {
        var changed = report.Rows.Where(r => r.Choice is not null && r.Skip is null).ToList();

        if (changed.Count == 0)
            return [];

        var path = rulesPath ?? UserRulesFile.DefaultPath;

        if (File.Exists(path))
        {
            File.Copy(
                path,
                Path.Combine(Path.GetDirectoryName(path) ?? "", "rules.user.before-autopick.yaml"),
                overwrite: true);
        }

        var file = UserRulesFile.Load(path);
        var entries = new List<(AutoPickRow Row, AutoPickJournalEntry Entry, List<AutoPickJournalEntry> Undone)>();
        var outcomes = new List<(AutoPickRow Row, AutoPickOutcome Outcome)>();

        // Свои правила *.имя, которые прежде записал сам автоподбор: у имени сервиса
        // это дубль его маршрута (26.10: «Netflix» и рядом netflix.com со своим
        // рецептом). Своё правило бьёт список, так что дубль молча перекрывал сервис.
        // От новых к старым: прогоны пишут цепочкой (Before одного — Wrote прошлого),
        // и снимать её можно только с конца.
        var previous = AutoPickJournal.Load(journalPath)
            .SelectMany(r => r.Entries)
            .Where(e => !e.Restored && e.Wrote is { Match: MatchKind.Domain } && e.Pins is null)
            .Reverse()
            .ToList();

        // Один список — одна запись: вторая строка того же маршрута затёрла бы первую.
        var lists = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Имена, уже стоящие в hosts (чужие либо наши, в том числе выключенные
        // строки нашего блока): их не перезаписываем.
        var present = changed.Any(r => r.Choice!.Way == AutoPickWay.Pin)
            ? HostsFile.Read(hostsPath).Keys.Concat(HostsEditor.OwnNames(hostsPath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        foreach (var row in changed)
        {
            var choice = row.Choice!;

            Dictionary<string, string>? pins = choice.Way == AutoPickWay.Pin
                ? row.Pins?.Where(p => !present.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value)
                : null;

            if (choice.Way == AutoPickWay.Pin && pins is not { Count: > 0 })
            {
                outcomes.Add((row, new AutoPickOutcome(row.Host, false, "нет имён для пина")));
                continue;
            }

            // Своё правило «десинк» ровно на это имя при пине «Маршруты» и проверка
            // объявили бы спором («рецепт не применится ни разу»): как окно «Файл hosts»,
            // ставим ему «напрямую» — общим путём ниже, с записью в журнал (аудит 26.10).
            bool ownDesync = pins is not null && row.Target.List is null
                && file.Entries.Any(e => e.Match == MatchKind.Domain && e.Enabled && !e.Off
                    && e.Mode == RoutingMode.Desync && e.Matches(SiteFixer.RuleValue(row.Host)));

            // Не через VPN сейчас — правила не нужно, hosts бьёт любое разрешение имени.
            if (pins is not null && row.CurrentMode != RoutingMode.Proxy && !ownDesync)
            {
                entries.Add((row, new AutoPickJournalEntry
                {
                    Host = row.Host,
                    Value = SiteFixer.RuleValue(row.Host),
                    Why = choice.Why,
                    Pins = pins,
                }, []));
                continue;
            }

            // Имя сервиса — в маршрут его части, своё имя — правилом на домен.
            var (match, value) = row.Target.List is { } list
                ? (MatchKind.HostList, list)
                : (MatchKind.Domain, SiteFixer.RuleValue(row.Host));

            if (match == MatchKind.HostList && !lists.TryAdd(value, row.Host))
            {
                outcomes.Add((row, new AutoPickOutcome(row.Host, false,
                    $"тот же маршрут уже записан по имени {lists[value]}")));
                continue;
            }

            // Выключенное человеком правило не включаем, даже если строка сюда дошла.
            if (file.Entries.FirstOrDefault(e => e.Match == match && e.Matches(value)) is { Off: true } or { Enabled: false })
            {
                outcomes.Add((row, new AutoPickOutcome(row.Host, false, "часть выключена в «Маршрутах» — не трогаю")));
                continue;
            }

            // Пустой рецепт храним как null: файл пустое не пишет, и запись,
            // прочитанная обратно, иначе не совпала бы с записанной.
            var recipe = choice.Mode == RoutingMode.Desync && !string.IsNullOrEmpty(choice.Recipe)
                ? choice.Recipe
                : null;

            var undone = new List<AutoPickJournalEntry>();

            if (match == MatchKind.HostList)
            {
                var duplicate = SiteFixer.RuleValue(row.Host);

                foreach (var stale in previous.Where(e => string.Equals(e.Value, duplicate,
                             StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    // Не снялась (человек правил дубль после записи) — его выбор, и
                    // старшие звенья цепочки под ней тоже не трогаем.
                    if (AutoPickJournal.TryUndo(file, stale) is not null)
                        break;

                    undone.Add(stale);
                    previous.Remove(stale);
                }
            }

            var before = file.Entries.FirstOrDefault(e => e.Match == match && e.Matches(value));

            var wider = match == MatchKind.HostList
                ? row.Target.Within
                : file.Entries.FirstOrDefault(e => e.Match == MatchKind.Domain && e.Enabled
                    && !e.Matches(value) && Covers(e.Value, row.Host))?.Value;

            file.Set(match, value, choice.Mode, recipe, wider);

            entries.Add((row, new AutoPickJournalEntry
            {
                Host = row.Host,
                Value = value,
                Before = before,
                Wrote = file.Entries.First(e => e.Match == match && e.Matches(value)),
                Why = choice.Why,
                Pins = pins,
            }, undone));
        }

        file.Save();

        if (evaluate is null && rulesPath is null)
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var (engine, _) = RuleSetExpander.LoadFor(settings);

            evaluate = host =>
            {
                var decision = engine.Evaluate(ConnectionEvent.Describe(host));

                // С учётом выбора движков — как CurrentMode при подборе: иначе правило
                // «VPN» при выключенном туннеле перекрывало бы пин понапрасну.
                return (settings.Engines.Effective(decision.Mode, settings.NeedsProxy),
                    decision.Rule?.Recipe, decision.Rule?.ToString() ?? decision.Reason);
            };
        }

        var kept = new List<AutoPickJournalEntry>();
        var retired = new List<AutoPickJournalEntry>();
        bool reverted = false;

        foreach (var (row, entry, undone) in entries)
        {
            var got = evaluate?.Invoke(row.Host);

            // Пин без правила перекрыт, только если имя всё ещё уходит в VPN: туннель
            // идёт по правилу, а не по адресу, и hosts его не обходит.
            bool shadowed = got is { } g
                && (entry.Wrote is null
                    ? g.Mode == RoutingMode.Proxy
                    : g.Mode != entry.Wrote.Mode
                        || (entry.Wrote.Mode == RoutingMode.Desync
                            && !string.Equals(g.Recipe ?? "", entry.Wrote.Recipe ?? "", StringComparison.Ordinal)));

            if (shadowed)
            {
                AutoPickJournal.TryUndo(file, entry);

                // Запись в сервис не встала — снятый ради неё дубль возвращается,
                // иначе имя молча лишилось бы рабочего маршрута. Со старших звеньев.
                foreach (var stale in Enumerable.Reverse(undone))
                    Redo(file, stale);

                reverted = true;
                outcomes.Add((row, new AutoPickOutcome(row.Host, false, $"перекрыто правилом {got!.Value.Rule}")));
            }
            else
            {
                kept.Add(entry);
                retired.AddRange(undone);
                outcomes.Add((row, new AutoPickOutcome(row.Host, true, entry.Why)));
            }
        }

        // Один вызов на все имена: блок hosts переписывается целиком.
        var toPin = kept.Where(e => e.Pins is not null)
            .SelectMany(e => e.Pins!)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

        if (toPin.Count > 0)
        {
            var result = HostsEditor.Pin(toPin, hostsPath, "автоподбор Zephyron");

            if (result.Reverted is { } why)
            {
                // Защитник вернул hosts: «прибито» было бы неправдой, а правила под
                // пин (если писали) без пина нужны не больше, чем пин без них.
                foreach (var gone in kept.Where(e => e.Pins is not null).ToList())
                {
                    var index = entries.FindIndex(e => e.Entry == gone);

                    AutoPickJournal.TryUndo(file, gone);

                    foreach (var stale in Enumerable.Reverse(entries[index].Undone))
                        Redo(file, stale);

                    retired.RemoveAll(entries[index].Undone.Contains);
                    kept.Remove(gone);
                    outcomes[outcomes.FindIndex(o => o.Row == entries[index].Row)] =
                        (entries[index].Row, new AutoPickOutcome(entries[index].Row.Host, false, why));
                }

                reverted = true;
                toPin.Clear();
            }
        }

        try
        {
            // Внутри try: сбой сброса DNS не должен оставить пины без журнала.
            if (toPin.Count > 0 && hostsPath is null)
                HostsEditor.FlushDns();

            if (reverted)
                file.Save();

            if (kept.Count > 0 || retired.Count > 0)
            {
                AutoPickJournal.Append(
                    new AutoPickJournalRun
                    {
                        At = DateTimeOffset.Now,
                        Preset = report.Preset,
                        Timings = new Dictionary<string, TimeSpan>(report.Timings),
                        Entries = kept,
                    },
                    journalPath,
                    retired);
            }
        }
        catch
        {
            // Пины без записи в журнале никто не снимет: убираем их, а правила вернёт Apply.
            if (toPin.Count > 0)
            {
                HostsEditor.Unpin(toPin.Keys.ToList(), hostsPath);

                if (hostsPath is null)
                    HostsEditor.FlushDns();
            }

            throw;
        }

        // Строки помечаются только теперь: сбой выше откатывает файл (Apply),
        // и «записано» у строки было бы неправдой.
        foreach (var (row, outcome) in outcomes)
        {
            row.Written = outcome.Written;
            row.Note = outcome.Note;
        }

        // В порядке строк: отказ «тот же маршрут уже записан» встаёт в список раньше прочих.
        return outcomes.OrderBy(o => changed.IndexOf(o.Row)).Select(o => o.Outcome).ToList();
    }

    /// <summary>Ставит обратно правило, снятое <see cref="AutoPickJournal.TryUndo"/>.</summary>
    private static void Redo(UserRulesFile file, AutoPickJournalEntry entry)
    {
        var wrote = entry.Wrote!;

        for (int i = 0; i < file.Entries.Count; i++)
        {
            if (file.Entries[i].Match == wrote.Match && file.Entries[i].Matches(entry.Value))
            {
                file.ReplaceAt(i, wrote);
                return;
            }
        }

        file.Set(wrote.Match, wrote.Value, wrote.Mode, wrote.Recipe, server: wrote.Server);
    }

    /// <summary>Накрывает ли правило-зона это имя.</summary>
    private static bool Covers(string value, string host)
    {
        var zone = value.TrimStart('*', '.');

        return string.Equals(zone, host, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase);
    }
}
