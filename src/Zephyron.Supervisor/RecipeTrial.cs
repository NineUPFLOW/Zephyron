using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Zephyron.Zapret;

namespace Zephyron.Supervisor;

/// <summary>
/// Проба «открывается ли имя при таком рецепте» — общая для окна выбора
/// рецепта и для автоподбора маршрутов.
/// </summary>
/// <remarks>
/// Вынесено из окна, чтобы автоподбор мерил той же меркой, что и человек
/// в окне: разные мерки в двух местах расходились бы в ответах, а сравнивать
/// предстоит именно их. Окно держит только то, что про экран.
/// </remarks>
public static class RecipeTrial
{
    /// <summary>
    /// Собирает аргументы winws2 с одним профилем под рецепт.
    /// </summary>
    /// <remarks>
    /// Профиль ровно один, без остального пресета: иначе имя могла бы забрать
    /// чужая секция, и замер сказал бы о ней, а не о выбранном рецепте.
    /// </remarks>
    public static IReadOnlyList<string> RecipeArguments(
        ZapretPreset preset,
        string zapretRoot,
        IReadOnlyList<string> names,
        IReadOnlyList<string> steps,
        string listPath)
    {
        // Глобальные ключи пресета обязательны, и это выяснилось дорого:
        // без них проверка отвечала «не помогает» на каждый рецепт подряд,
        // включая заведомо рабочие. В них три вещи, без которых рецепт —
        // пустой звук:
        //
        //   --lua-init   загружает библиотеку, где эти самые fake,
        //                multidisorder и hostfakesplit_multi определены;
        //                без неё имя в --lua-desync не значит ничего;
        //   --wf-*       фильтр WinDivert, то есть какой трафик вообще
        //                перехватывать; без него не перехватывается ничего;
        //   --blob       заготовки поддельных пакетов, на которые рецепты
        //                ссылаются по имени (blob=tls_google).
        var arguments = new List<string>(preset.GlobalArguments)
        {
            // Профиль собирается теми же ключами, что уйдут в работу. Разойдись
            // они — проверка мерила бы не то, что потом применится, и это
            // не теория: без --out-range рецепт открывал домен для curl,
            // а sing-box получал от того же сервера отказ в рукопожатии.
            "--new",
            "--name=Zephyron: проба",

            // Порты те же, что у секции, которая это имя забирает. Зашитое
            // 80,443 мерило бы не то, что применится: у обложек Spotify секция
            // объявлена на одном 443, а у Discord - на восьми портах.
            "--filter-tcp=" + PresetPorts.ForDomains(preset, zapretRoot, names),
            $"--hostlist={WinwsCommandLine.Forward(Path.GetFullPath(listPath))}",
            WinwsCommandLine.ProbeOutRange,
        };

        arguments.AddRange(steps.Select(step => $"--lua-desync={step}"));

        return arguments;
    }

    /// <summary>
    /// Поднимает winws2 с этими аргументами, ждёт готовности, исполняет
    /// <paramref name="body"/> и гасит процесс.
    /// </summary>
    /// <returns>
    /// <c>null</c> — тело исполнено; иначе причина, по которой измерять было
    /// не при чём. Ушедший до готовности winws2 — отдельная причина: без неё
    /// проба молча мерила бы голое соединение и выдавала его за рецепт.
    /// </returns>
    public static async Task<string?> WithWinwsAsync(
        string winws,
        string zapretRoot,
        IReadOnlyList<string> arguments,
        Func<Task> body,
        CancellationToken cancellationToken)
    {
        // Папка над --writable пресета: без неё winws2 не стартует вовсе.
        WinwsCommandLine.PrepareWritable(arguments, zapretRoot);

        var start = new ProcessStartInfo(winws)
        {
            WorkingDirectory = zapretRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        Process? process = null;

        try
        {
            process = Process.Start(start);

            if (process is null)
                return "winws2 не запустился";

            // Ждём, пока драйвер встанет в разрыв, — но по его же словам,
            // а не по часам. Раньше здесь стояли глухие две секунды на каждый
            // рецепт: на десятке это двадцать секунд чистого ожидания, и почти
            // всё оно лишнее — winws2 обычно готов за треть секунды.
            await ReadyAsync(process, cancellationToken);

            if (process.HasExited)
                return "winws2 не поднялся";

            await body();

            return null;
        }
        finally
        {
            try
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(Leaves);
                }
            }
            catch (Exception)
            {
                // Процесс мог уйти сам — например, отказавшись от кривого
                // рецепта. Это и есть его отрицательный результат.
            }

            process?.Dispose();
        }
    }

    /// <summary>
    /// Ждёт, пока winws2 доложит о готовности.
    /// </summary>
    /// <remarks>
    /// Ждём ровно ту строку, которой он сообщает о поставленном фильтре:
    /// раньше неё пакеты не перехватываются вовсе, и проба мерила бы
    /// незащищённое соединение. Прежде здесь ждали строку про число профилей —
    /// она печатается при разборе настроек, то есть до того, как драйвер
    /// встал в разрыв.
    /// </remarks>
    private static async Task ReadyAsync(Process process, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);

        var line = process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();

        while (DateTime.UtcNow < deadline)
        {
            var finished = await Task.WhenAny(line, Task.Delay(300, cancellationToken));

            if (finished != line)
                continue;

            if (line.Result is null)
                break;

            // «windivert initialized. capture is started.» — драйвер в разрыве.
            if (line.Result.Contains("capture is started", StringComparison.OrdinalIgnoreCase))
                break;

            line = process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();
        }

        // Выходные потоки дальше никто не читает, а winws2 живёт минутами
        // (автоподбор держит его на весь прогон): забитая труба остановила бы
        // его на записи в журнал. Стандартный вывод сливаем, только если
        // чтение закончено — иначе два читателя на одном потоке.
        _ = Drain(process.StandardError);

        // Если чтение ещё висит (вышли по сроку), сливаем со следующего же его
        // завершения: до тех пор читатель на потоке один - оно само.
        _ = line.ContinueWith(
            t => t.IsCompletedSuccessfully ? Drain(process.StandardOutput) : Task.CompletedTask,
            TaskScheduler.Default);

        // Небольшая доводка всё же нужна: между разбором настроек и первым
        // перехваченным пакетом драйвер успевает не всегда.
        await Task.Delay(Settle, cancellationToken);
    }

    private static async Task Drain(StreamReader reader)
    {
        try
        {
            await reader.BaseStream.CopyToAsync(Stream.Null);
        }
        catch (Exception)
        {
            // Процесс снят — поток закрылся, сливать больше нечего.
        }
    }

    /// <summary>
    /// Открывается ли имя: одно рукопожатие TLS, и всё.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Полная проба спрашивает TCP, TLS 1.2, TLS 1.3, HTTP и передачу данных —
    /// пять ответов там, где нужен один. Рецепт чинит ровно приветствие TLS,
    /// и вопрос к нему ровно один: дошло ли оно. На десятке рецептов разница
    /// между «одно рукопожатие» и «пять проб» — это минуты.
    /// </para>
    /// <para>
    /// Чужой сертификат и любой ответ об ошибке считаются успехом: отвечает
    /// сервер — значит приветствие дошло, а это и есть предмет замера.
    /// Проверять имя на сертификате тут незачем и вредно: у выдуманного
    /// имени пробы его не будет никогда.
    /// </para>
    /// <para>
    /// Адрес приходит параметром и дальше живёт локально: автоподбор мерит
    /// несколько имён параллельно, и общее поле перемешало бы их.
    /// </para>
    /// </remarks>
    public static async Task<Flow> OpensAsync(
        string host,
        IPAddress address,
        CancellationToken cancellationToken)
    {
        var where = host;
        var path = "/";
        Flow last = Flow.Nothing("не начинали");

        // Перенаправления проходим до конца, а не останавливаемся на первом.
        //
        // Владелец 21.09: «результат не изменился, только обёртка другая» —
        // и был прав. Назвать 301 перенаправлением мало: это не вердикт,
        // а недоведённый замер. Instagram отвечает 301 на apex, браузер идёт
        // дальше и там-то и ломается, а проверка останавливалась и говорила
        // «фильтр пройден».
        //
        // Три шага: больше нужно разве что кольцу, а кольцо — само по себе
        // отказ. Каждый шаг идёт своим соединением: предыдущее закрыто
        // по нашей же просьбе Connection: close.
        for (int hop = 0; hop < 3; hop++)
        {
            last = await FetchAsync(where, path, address, cancellationToken);

            if (!last.Passed || last.Page || last.Location is not { Length: > 0 } next)
                return last;

            if (!Uri.TryCreate(next, UriKind.Absolute, out var target))
            {
                // Относительный адрес — тот же хост, другой путь.
                path = next.StartsWith('/') ? next : "/" + next;
                continue;
            }

            // Уводит на чужую зону — дальше не наше дело: там другое имя,
            // другой фильтр и другой разговор. Но сказать об этом надо.
            if (!SameZone(target.Host, host))
                return last with { Ending = $"уводит на {target.Host}" };

            where = target.Host;
            path = target.PathAndQuery;

            // Имя сменилось — сменился и адрес. Список проверки покрывает
            // зону целиком, так что десинк к нему применится тот же.
            if (!string.Equals(where, host, StringComparison.OrdinalIgnoreCase))
                address = await AddressForAsync(where, cancellationToken) ?? address;
        }

        return last with { Ending = "перенаправления не кончаются" };
    }

    /// <summary>Одно ли это дерево имён.</summary>
    /// <remarks>
    /// Апекс на www и обратно — самое частое перенаправление в сети,
    /// и обрывать на нём замер значило бы не доводить его никогда.
    /// А вот уход на чужую зону — это уже другое имя и другой разговор.
    /// </remarks>
    private static bool SameZone(string one, string other)
    {
        var a = one.TrimStart('*', '.');
        var b = other.TrimStart('*', '.');

        return a.Equals(b, StringComparison.OrdinalIgnoreCase)
            || a.EndsWith("." + b, StringComparison.OrdinalIgnoreCase)
            || b.EndsWith("." + a, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<Flow> FetchAsync(
        string host,
        string path,
        IPAddress address,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Reach);

            await client.ConnectAsync(address, 443, deadline.Token);

            bool certValid = false;

            await using var tls = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, _, _, errors) =>
                {
                    certValid = errors == System.Net.Security.SslPolicyErrors.None;
                    return true;
                });

            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
            }, deadline.Token);

            return (await FlowsAsync(tls, host, path, cancellationToken)) with { CertValid = certValid };
        }
        catch (Exception ex)
        {
            // Молчание, сброс, отказ — до сервера не дошли. Но чем именно
            // кончилось, сказать стоит: «сброс» и «тишина» лечатся разным,
            // и прежде оба выглядели одинаковым «не помогает».
            //
            // Но отмена вызывающим — не исход пробы: записанная как «не дождались»,
            // она ложилась бы в отчёт ложной неудачей. Свой срок (deadline) — исход.
            cancellationToken.ThrowIfCancellationRequested();
            return Flow.Nothing(Short(ex));
        }
    }

    /// <summary>
    /// Сколько данных считать доказательством, что рецепт работает.
    /// </summary>
    /// <remarks>
    /// Шестнадцать килобайт — выше всех порогов, на которых у нас рвались
    /// потоки: steamcommunity.com умирал на 14 381 Б, skinsrestorer.net
    /// на 13 505, Steam на 16 384. Меньший порог такие обрывы пропустил бы,
    /// а больший стоил бы времени на каждом из восьми рецептов.
    /// </remarks>
    private const int Enough = 16 * 1024;

    /// <summary>Сколько ждать продолжения, прежде чем счесть поток убитым.</summary>
    private static readonly TimeSpan Silence = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Сколько ждать соединения и рукопожатия.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Главный расход всего перебора, и вот почему. Провальных рецептов
    /// в списке большинство, а проваливаются они одинаково: TCP встаёт
    /// мгновенно — фильтр его пропускает, — и висит рукопожатие, до самого
    /// предела. То есть предел этот платится за каждый неработающий рецепт
    /// целиком.
    /// </para>
    /// <para>
    /// Было четыре секунды, стало две с половиной. Рабочее рукопожатие
    /// на замерах владельца проходит за 0,8 с, так что запас остаётся
    /// трёхкратный, а на одиннадцати рецептах это экономит около девяти
    /// секунд — больше, чем все прочие сокращения вместе.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan Reach = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// Выдержка после того, как драйвер доложил о готовности.
    /// </summary>
    /// <remarks>
    /// Между разбором настроек и первым перехваченным пакетом winws2
    /// успевает не всегда, и совсем без выдержки проба мерила бы
    /// незащищённое соединение. Но четыреста миллисекунд брались с запасом
    /// на глаз; двухсот пятидесяти хватает, а на одиннадцати рецептах
    /// это ещё полторы секунды.
    /// </remarks>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Сколько ждать, пока снятый процесс уйдёт.
    /// </summary>
    /// <remarks>
    /// Ждём не ухода как такового, а освобождения WinDivert: не дождавшись,
    /// следующий рецепт не поднимется. Четыре секунды здесь были запасом
    /// на случай, которого не случалось ни разу, — winws2 уходит за
    /// сотни миллисекунд.
    /// </remarks>
    private const int Leaves = 1500;

    /// <summary>
    /// Что написать в строке рецепта по итогам замера.
    /// </summary>
    /// <remarks>
    /// Отдельно и статически — единственная чистая часть этого перебора,
    /// и единственная, которую можно проверить тестом. Слова здесь важнее
    /// обычного: прежний вердикт «открывается за 0,8 с» читался как
    /// утверждение о сайте, тогда как сказано им было о рукопожатии.
    /// </remarks>
    public static string Verdict(Flow flow, TimeSpan took)
    {
        var seconds = $"{took.TotalSeconds:0.0} с";

        if (flow.Stub)
            return $"заглушка: сайт отказывает по стране (ответ {flow.Status}), {seconds}";

        // Ответ с настоящим сертификатом на имя — это сервер, а не заглушка:
        // так же считает AutoPick.Works(Flow), и вердикт не должен ему перечить.
        if (!flow.Page && flow.CertValid && flow.Status is >= 300 and < 500 and not 451)
        {
            return flow.Status >= 400
                ? $"ответ {flow.Status}, сертификат верный — сервер отвечает, {seconds}"
                : $"перенаправление {flow.Status}, сертификат верный — сервер отвечает ({flow.Ending}), {seconds}";
        }

        if (!flow.Passed)
            return "не помогает — " + flow.Ending;

        if (flow.Page)
            return $"страница {flow.Status}, {flow.Bytes} Б, {seconds}";

        // Сюда попадает перенаправление, которое мы не смогли довести
        // до конца: увело на чужую зону либо закольцевалось. Фильтр им
        // пройден, но страницы нет, и в счёт открывших такое не идёт —
        // иначе выходит «открывают 5 из 11» при неработающем сайте.
        return $"фильтр пройден, страницы нет — {flow.Ending}";
    }

    /// <summary>
    /// Чем кончился обмен после рукопожатия.
    /// </summary>
    /// <remarks>
    /// Не «да/нет», и это исправление 21.09. Прежде успехом считался любой
    /// пришедший байт, и окно отвечало «открывается за 0,8 с» про имя,
    /// которое в браузере не грузилось вовсе. Байты и вправду приходили —
    /// короткое перенаправление на страницу входа, — но по такому ответу
    /// нельзя сказать ни «работает», ни «не работает», не назвав его.
    /// </remarks>
    public sealed record Flow
    {
        public required int Bytes { get; init; }

        /// <summary>Код ответа; <c>null</c> — ответ не похож на HTTP.</summary>
        public int? Status { get; init; }

        /// <summary>Куда перенаправляют, если перенаправляют.</summary>
        public string? Location { get; init; }

        /// <summary>Чем кончилось — словами, для вердикта.</summary>
        public required string Ending { get; init; }

        /// <summary>
        /// Рецепт провёл соединение мимо фильтра.
        /// </summary>
        /// <remarks>
        /// Признак — разобранный код ответа, а не объём. Ответивший сервер
        /// означает, что до него дошли: ни фильтр, ни заглушка внятного
        /// HTTP по этому имени не отдадут. Перенаправление сюда входит —
        /// апекс на www перенаправляет половина интернета, и звать это
        /// отказом значило бы врать в другую сторону.
        /// </remarks>
        public bool Passed => Status is >= 200 and < 400;

        /// <summary>
        /// Сертификат сервера действителен для имени пробы.
        /// </summary>
        /// <remarks>
        /// Проверка принимает любой сертификат, чтобы измерять доставку, а не
        /// доверие. Но ответ 4xx сам по себе слаб: его отдаёт и заглушка. Настоящий
        /// сертификат на имя заглушке взять негде.
        /// </remarks>
        public bool CertValid { get; init; }

        /// <summary>Пришла настоящая страница, а не перенаправление.</summary>
        public bool Page => Status is >= 200 and < 300;

        /// <summary>
        /// Короткий ответ с отказом по стране: «Not Available», «недоступно в вашей стране».
        /// </summary>
        /// <remarks>
        /// Владелец 26.10: netflix.com в браузере — «Not Available», а подбор считал
        /// его открытым. Сайт отвечает, связь исправна, но страницы нет — и таких
        /// сайтов у человека может быть много.
        /// </remarks>
        public bool Stub { get; init; }

        public static Flow Nothing(string ending) => new() { Bytes = 0, Ending = ending };
    }

    /// <summary>
    /// Идут ли данные после того, как рукопожатие состоялось.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Этот урок мы уже оплатили однажды — в проверке блокировок. Там
    /// состоявшееся рукопожатие перестало считаться ответом после разбора
    /// steamcommunity.com: он не открывался, а проверка называла его
    /// доступным, потому что рукопожатие и вправду проходило — поток умирал
    /// после, на четырнадцатой тысяче байт.
    /// </para>
    /// <para>
    /// Окно выбора рецепта того урока не получило и продолжало отвечать
    /// по одному рукопожатию. Отсюда и «три рецепта из восьми открывают»
    /// у раздачи Spotify при неработающих обложках: приветствие доходило
    /// со всеми тремя, а картинка не приходила ни с одним.
    /// </para>
    /// <para>
    /// Закрытое сервером соединение — успех, а не отказ: короткая страница
    /// кончается раньше порога, и ждать от неё шестнадцати килобайт незачем.
    /// Отказ — это тишина при живом соединении.
    /// </para>
    /// </remarks>
    private static async Task<Flow> FlowsAsync(
        SslStream tls,
        string host,
        string path,
        CancellationToken cancellationToken)
    {
        var request = System.Text.Encoding.ASCII.GetBytes(
            $"GET {path} HTTP/1.1\r\nHost: {host}\r\n"
            + "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64)\r\n"
            + "Accept: */*\r\nAccept-Encoding: identity\r\nConnection: close\r\n\r\n");

        await tls.WriteAsync(request, cancellationToken);

        var buffer = new byte[16 * 1024];
        var head = new MemoryStream();
        int total = 0;
        string ending = "дочитали до порога";

        while (total < Enough)
        {
            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            quiet.CancelAfter(Silence);

            int read;

            try
            {
                read = await tls.ReadAsync(buffer, quiet.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Тишина при живом соединении — то самое, что ищем.
                return Flow.Nothing(total == 0 ? "тишина" : $"оборвалось на {total} Б");
            }
            catch (Exception ex)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Flow.Nothing(total == 0
                    ? Short(ex)
                    : $"оборвалось на {total} Б: {Short(ex)}");
            }

            if (read == 0)
            {
                ending = "сервер закрыл соединение";
                break;
            }

            // Заголовок копим до первой пустой строки: код и Location лежат
            // в нём, а тело нам неинтересно — мы не браузер.
            // Байтами, а разбираем разом в UTF-8: по началу тела узнаётся заглушка,
            // она бывает по-русски, а буква на стыке двух чтений иначе разломилась бы.
            if (head.Length < 4096)
                head.Write(buffer, 0, read);

            total += read;
        }

        return Parse(System.Text.Encoding.UTF8.GetString(head.GetBuffer(), 0, (int)head.Length), total, ending);
    }

    /// <summary>
    /// Разбирает начало ответа: код и, если есть, куда перенаправляют.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Код ответа — единственный внятный признак того, что до сервера дошли.
    /// Ни фильтр, ни заглушка оператора по этому имени внятного HTTP
    /// не отдадут: для этого нужен сертификат на него, а его у них нет.
    /// </para>
    /// <para>
    /// Не разобралось — считаем, что не дошли. Прежде здесь хватало любого
    /// байта, и в успех попадало всё подряд, включая обрывки, по которым
    /// сказать ничего нельзя.
    /// </para>
    /// </remarks>
    private static Flow Parse(string head, int total, string ending)
    {
        var first = head.Split('\n', 2)[0].Trim();

        if (!first.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)
            || first.Split(' ') is not [_, var code, ..]
            || !int.TryParse(code, out int status))
        {
            return new Flow
            {
                Bytes = total,
                Ending = total == 0 ? ending : $"{total} Б, и это не похоже на HTTP",
            };
        }

        string? where = null;

        foreach (var line in head.Split('\n'))
        {
            if (line.StartsWith("Location:", StringComparison.OrdinalIgnoreCase))
            {
                where = line["Location:".Length..].Trim();
                break;
            }
        }

        return new Flow
        {
            Bytes = total,
            Status = status,
            Location = where,
            Ending = ending,
            Stub = Refused(head, total, status, where),
        };
    }

    /// <summary>
    /// Отказ по стране — по тем же признакам, что в проверке блокировок (BlockCheck).
    /// </summary>
    /// <remarks>
    /// 403/451 без проверки на робота, перенаправление на страницу отказа
    /// (claude.ai → app-unavailable-in-region), короткая заглушка «Not Available».
    /// Проверку на робота Cloudflare (403 с <c>cf-mitigated: challenge</c>) браузер
    /// проходит сам: 26.10 npmjs.com и fandom.com из-за неё уходили в VPN как
    /// «отказывает по стране», открываясь напрямую.
    /// </remarks>
    public static bool Refused(string head, int total, int status, string? location)
    {
        int end = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var headers = (end < 0 ? head : head[..end]).ToLowerInvariant();

        if (headers.Contains("\ncf-mitigated: challenge", StringComparison.Ordinal))
            return false;

        if (status is 403 or 451)
            return true;

        if (status is >= 300 and < 400 && Uri.TryCreate(location, UriKind.Absolute, out var target)
            && Zephyron.Proxy.PinPicker.IsRegionRefusal(target))
        {
            return true;
        }

        return IsStub(head, total);
    }

    /// <summary>Фразы отказа по стране; ищутся в теле в нижнем регистре.</summary>
    private static readonly string[] StubWords =
    [
        "not available", "unavailable in your", "isn't available in your", "not supported in your",
        "in your country", "in your region", "access denied", "error 1009", "banned the country",
        "недоступ", "в вашей стране", "в вашем регионе",
    ];

    /// <summary>
    /// Заглушка ли это: тело короткое и говорит об отказе по месту.
    /// </summary>
    /// <remarks>
    /// Только короткое: настоящая страница весит десятки килобайт и может сама
    /// содержать «not available» где-нибудь в подвале. Netflix отдаёт 13 байт.
    /// </remarks>
    public static bool IsStub(string head, int total)
    {
        if (total > 4096)
            return false;

        int start = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);

        // Ответ API ({"error":"access denied"}) — не страница для человека: у служебного
        // адреса это обычный ответ, а не отказ по стране.
        var headers = (start < 0 ? head : head[..start]).ToLowerInvariant();

        if (headers.Contains("content-type:", StringComparison.Ordinal) && headers.Contains("json", StringComparison.Ordinal))
            return false;
        var body = (start < 0 ? "" : head[(start + 4)..]).ToLowerInvariant();

        return StubWords.Any(w => body.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>
    /// Короткое объяснение обрыва.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Своими словами, а не обрезанное системное. Windows отвечает
    /// «Удаленный хост принудительно разорвал существующее подключение»,
    /// и обрезка по длине давала «…принудительно разорвал су» — оборванное
    /// посреди слова и ничего не добавляющее к «не помогает».
    /// </para>
    /// <para>
    /// Три исхода, и различать их стоит: сброс — это DPI ответил, отказ
    /// в рукопожатии — сторона не приняла наше приветствие, тишина —
    /// до неё не дошло вовсе. Лечатся они разным.
    /// </para>
    /// </remarks>
    public static string Short(Exception ex)
    {
        var why = ex.GetBaseException();

        if (why is System.Security.Authentication.AuthenticationException)
            return "отказ в рукопожатии";

        if (why is OperationCanceledException or TimeoutException)
            return "не дождались";

        if (why is SocketException socket)
        {
            return socket.SocketErrorCode switch
            {
                SocketError.ConnectionReset => "соединение сброшено",
                SocketError.ConnectionRefused => "в соединении отказано",
                SocketError.TimedOut => "не дождались",
                SocketError.HostUnreachable => "хост недостижим",
                SocketError.NetworkUnreachable => "сеть недостижима",
                _ => "обрыв связи",
            };
        }

        // Сброс приходит и завёрнутым в IOException — от SslStream
        // и от потока сокета. Разворачивать до конца не всегда выходит,
        // поэтому смотрим ещё и по вложенному.
        for (var inner = ex; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException nested)
                return Short(nested);
        }

        var said = why.Message;

        return said.Length > 40 ? said[..40] + "…" : said;
    }

    /// <summary>
    /// Адрес, куда стучаться приветствием с этим именем.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Имя пробы может не разрешаться вовсе, и это не помеха: фильтр читает
    /// его из приветствия TLS, а не из DNS. Голосовые серверы Discord
    /// выдаются под звонок, постоянного имени у них нет, и проверять зону
    /// больше нечем — <c>frankfurt1234.discord.media</c> сегодня отвечает
    /// пустотой у любого резолвера.
    /// </para>
    /// <para>
    /// Поэтому адрес берётся у ближайшего предка, который разрешается:
    /// он в той же зоне и стоит за той же сетью доставки. Цепочку предков
    /// строит <see cref="HostNames.ZoneChain"/>, там же и решено, где
    /// остановиться.
    /// </para>
    /// </remarks>
    public static async Task<IPAddress?> AddressForAsync(
        string host,
        CancellationToken cancellationToken)
    {
        foreach (var name in HostNames.ZoneChain(host))
        {
            try
            {
                var found = await Dns.GetHostAddressesAsync(name, cancellationToken);

                var address = found.FirstOrDefault(a =>
                    a.AddressFamily == AddressFamily.InterNetwork) ?? found.FirstOrDefault();

                if (address is not null)
                    return address;
            }
            catch (Exception)
            {
                // Не разрешилось — спросим про предка.
            }
        }

        return null;
    }
}
