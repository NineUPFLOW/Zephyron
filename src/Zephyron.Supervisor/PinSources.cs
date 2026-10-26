using Zephyron.Core;
using Zephyron.Core.Rules;
using Zephyron.Proxy;
using Zephyron.Zapret;

namespace Zephyron.Supervisor;

/// <summary>
/// Откуда брать адреса для пина: общее для окна «Файл hosts» и автоподбора.
/// </summary>
public static class PinSources
{
    /// <summary>
    /// Кандидаты для <see cref="PinPicker.PickAsync"/>: известные про имя и посредники для любого.
    /// </summary>
    /// <param name="mains">Главные имена: прочие имена сайта пробуются лишь на лучших адресах главного.</param>
    public static async Task<(Func<string, IReadOnlyList<PinCandidate>> Known, IReadOnlyList<PinCandidate> Pool)> LoadAsync(
        IEnumerable<string> mains,
        ZapretCatalog? catalog,
        OwnCatalog own,
        CancellationToken cancellationToken)
    {
        // Что посредники отдают этим именам сейчас. Снимок каталога стареет:
        // 28.09 он знал у XBOX DNS только мёртвые .195 и .204, а живые .201
        // и .203 владелец нашёл в чужом GUI (IntermediaryDns).
        //
        // Только для главного имени: прочие имена пробуются лишь на лучших
        // адресах главного (PinPicker.PickAsync), и ответы посредников им
        // ни к чему, а вопрос пяти серверам на каждое из 48 имён Claude —
        // 240 запросов.
        var live = await IntermediaryDns.AskManyAsync(mains, cancellationToken);

        // Посредники — из живого каталога Zapret, где он стоит, и из снимка,
        // который едет с программой: без Zapret остался бы только второй.
        return (
            host => [.. own.PinCandidates(host), .. live.TryGetValue(host, out var now) ? now : [], .. catalog?.AnswersFor(host) ?? []],
            [.. catalog?.Intermediaries() ?? [], .. own.Intermediaries]);
    }
}
