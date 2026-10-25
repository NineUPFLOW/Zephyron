using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Zephyron.Gui.Views;

/// <summary>
/// Живая строка сети на «Главной»: приём, отдача, задержка, потери.
/// </summary>
/// <remarks>
/// <para>
/// Скорость — по счётчикам байтов всех работающих сетевых адаптеров машины,
/// раз в секунду. Адаптер туннеля (zephyron0, Wintun) и петля не считаются:
/// трафик туннеля уже посчитан на физическом адаптере, по которому он идёт
/// наружу, и иначе считался бы дважды.
/// </para>
/// <para>
/// Задержка — ICMP до 1.1.1.1 раз в пять секунд, потери — доля неответов
/// из последних двадцати четырёх, то есть за две минуты. Это задержка сети
/// машины, а не серверов подписки: те меряет «VPN» своим замером.
/// </para>
/// </remarks>
public partial class StatusView
{
    private static readonly TimeSpan NetInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(5);

    /// <summary>Сколько проверок задержки помнить для потерь — две минуты.</summary>
    private const int PingWindow = 24;

    /// <summary>Сколько секунд скорости на графике — минута.</summary>
    private const int SparkBars = 60;

    private const string PingTarget = "1.1.1.1";

    /// <summary>Имя адаптера туннеля — то же, что даёт ему конфиг движка.</summary>
    private static readonly string TunName = new Proxy.SingBoxOptions().TunInterfaceName;

    private readonly DispatcherTimer _net = new() { Interval = NetInterval };
    private readonly Queue<bool> _pings = new();
    private readonly Queue<double> _down = new();
    private readonly Queue<double> _up = new();

    /// <summary>Задержка проверок за две минуты, мс; <c>NaN</c> — ответа не было.</summary>
    private readonly Queue<double> _pingMs = new();

    private (long Received, long Sent, DateTime At)? _lastBytes;
    private DateTime _lastPing = DateTime.MinValue;
    private bool _netBusy;
    private bool _pingBusy;

    /// <summary>
    /// Меряет, пока обход работает, и стоит, пока нет.
    /// </summary>
    /// <remarks>
    /// Владелец 22.10: метрика — часть запуска, а не всегдашний прибор.
    /// Остановленная строка показывает прочерки «—», а не нули и не последнее
    /// измеренное: застывшие цифры читались бы как ещё идущий замер.
    /// </remarks>
    private void SyncNet(bool on)
    {
        if (on == _net.IsEnabled)
            return;

        if (on)
            StartNet();
        else
            StopNet();
    }

    private void StartNet()
    {
        ResetNet();

        _net.Tick -= OnNet;
        _net.Tick += OnNet;
        _net.Start();
        OnNet(null, EventArgs.Empty);
    }

    private void StopNet()
    {
        _net.Stop();
        ResetNet();
    }

    /// <summary>Строка без замеров: везде «—», пустые графики, частицам — «скорости нет».</summary>
    private void ResetNet()
    {
        _lastBytes = null;
        _lastPing = DateTime.MinValue;
        _pings.Clear();
        _down.Clear();
        _up.Clear();
        _pingMs.Clear();

        _mbps = null;
        NetUp.Opacity = 1;

        SetMetric(NetDown, NetDownUnit, null, "Accent2");
        SetMetric(NetUp, NetUpUnit, null, "Text");
        SetMetric(NetPing, NetPingUnit, null, "Accent2");
        SetMetric(NetLoss, NetLossUnit, null, "Warn");

        foreach (var cell in new FrameworkElement[] { NetDownCell, NetUpCell, NetPingCell, NetLossCell })
            cell.Opacity = 1;

        ShowSpark();
        ShowLatency();
        Pulse();
    }

    /// <summary>
    /// Число метрики с единицей или «—» (Muted) — когда замера нет. Не «0.0»:
    /// ноль — это замер, а прочерк — его отсутствие.
    /// </summary>
    private void SetMetric(TextBlock value, TextBlock unit, string? text, string colourKey)
    {
        value.Text = text ?? "—";
        value.SetResourceReference(TextBlock.ForegroundProperty, text is null ? "Muted" : colourKey);
        unit.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnNet(object? sender, EventArgs e)
    {
        if (!_netBusy)
        {
            _netBusy = true;

            try
            {
                // Перебор адаптеров — в пуле: на машине с десятком виртуальных
                // он занимает миллисекунды, но не в потоке окна.
                var bytes = await Task.Run(CountBytes);

                // Пока считали, обход могли остановить — строка уже сброшена.
                if (_net.IsEnabled)
                    ShowRates(bytes);
            }
            catch (Exception)
            {
                // Счётчики не прочитались — строка остаётся с прошлым значением.
            }
            finally
            {
                _netBusy = false;
            }
        }

        if (!_pingBusy && DateTime.UtcNow - _lastPing >= PingInterval)
            _ = PingAsync();
    }

    /// <summary>Сумма принятых и отданных байтов по адаптерам, которые несут трафик наружу.</summary>
    private static (long Received, long Sent) CountBytes()
    {
        long received = 0, sent = 0;

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up
                || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                || adapter.Name.StartsWith(TunName, StringComparison.OrdinalIgnoreCase)
                || adapter.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var stats = adapter.GetIPStatistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
            }
            catch (Exception)
            {
                // Адаптер пропал между перечислением и чтением — пропускаем.
            }
        }

        return (received, sent);
    }

    private void ShowRates((long Received, long Sent) bytes)
    {
        var now = DateTime.UtcNow;

        if (_lastBytes is not { } last)
        {
            _lastBytes = (bytes.Received, bytes.Sent, now);
            return;
        }

        _lastBytes = (bytes.Received, bytes.Sent, now);

        double seconds = (now - last.At).TotalSeconds;

        if (seconds <= 0.2)
            return;

        // Адаптер мог пропасть или появиться — счётчик тогда скачет назад.
        double down = Math.Max(0, bytes.Received - last.Received) * 8 / seconds / 1_000_000;
        double up = Math.Max(0, bytes.Sent - last.Sent) * 8 / seconds / 1_000_000;

        SetMetric(NetDown, NetDownUnit, Rate(down), "Accent2");
        SetMetric(NetUp, NetUpUnit, Rate(up), "Text");

        // Отдача — Text вполсилы (.75), чтобы не спорить с приёмом цветом.
        NetUp.Opacity = 0.75;

        // Тихая сеть — тише и строка: меньше 10 кбит/с читается как «ничего».
        NetDownCell.Opacity = down < 0.01 ? 0.45 : 1;
        NetUpCell.Opacity = up < 0.01 ? 0.45 : 1;

        // Настоящая скорость приёма — слою частиц; выдуманного не шлём.
        _mbps = down;
        Pulse();

        _down.Enqueue(down);
        _up.Enqueue(up);

        while (_down.Count > SparkBars)
            _down.Dequeue();

        while (_up.Count > SparkBars)
            _up.Dequeue();

        ShowSpark();
    }

    /// <summary>«0.0», «8.4», «312» — три значащих знака, без лишних нулей у больших.</summary>
    internal static string Rate(double mbps) =>
        mbps >= 100 ? mbps.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
            : mbps.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// График скорости за минуту (приём — Accent2 сплошной с заливкой, отдача —
    /// Text вполсилы пунктиром) и скорость потока по трассам — из того же замера.
    /// </summary>
    /// <remarks>
    /// До 0.18 здесь было сорок восемь прямоугольников, и смена их высоты раз
    /// в секунду заново раскладывала сетку. График рисуется одним проходом
    /// (NetChart) и доезжает до новой точки за секунду до следующей.
    /// </remarks>
    private void ShowSpark()
    {
        var down = _down.ToArray();

        ThroughputChart.Show(
            [new(down, (System.Windows.Media.Brush)FindResource("Accent2"), true),
             new(_up.ToArray(), (System.Windows.Media.Brush)FindResource("Text"), false, Dashed: true, Opacity: 0.75)],
            step: NetInterval);

        Lanes.Flow = down.Length > 0 ? down[^1] : 0;
    }

    /// <summary>График задержки за две минуты; потерянные проверки — ромбами Warn.</summary>
    private void ShowLatency()
    {
        var values = _pingMs.ToArray();
        var lost = Enumerable.Range(0, values.Length).Where(i => double.IsNaN(values[i])).ToArray();

        LatencyChart.Show(
            [new(values, (System.Windows.Media.Brush)FindResource("Accent2"), true)],
            marks: lost, step: PingInterval);
    }

    private async Task PingAsync()
    {
        _pingBusy = true;
        _lastPing = DateTime.UtcNow;

        long? ms = null;

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(PingTarget, 1500);

            if (reply.Status == IPStatus.Success)
                ms = reply.RoundtripTime;
        }
        catch (Exception)
        {
            // Нет сети или ICMP запрещён — считается потерей.
        }
        finally
        {
            _pingBusy = false;
        }

        if (!_net.IsEnabled)
            return;

        _pings.Enqueue(ms is not null);
        _pingMs.Enqueue(ms is { } answered ? answered : double.NaN);

        while (_pings.Count > PingWindow)
            _pings.Dequeue();

        while (_pingMs.Count > PingWindow)
            _pingMs.Dequeue();

        ShowLatency();

        // Нет ответа — «—», а не нуль: задержки нет, а не нулевая.
        SetMetric(NetPing, NetPingUnit, ms is { } value ? value.ToString() : null, "Accent2");
        NetPingCell.Opacity = ms is null ? 0.45 : 1;

        // Ноль потерь — настоящий замер (Text); потери есть — Warn.
        int lost = _pings.Count(ok => !ok);
        SetMetric(NetLoss, NetLossUnit, $"{100.0 * lost / _pings.Count:0}", lost == 0 ? "Text" : "Warn");
        NetLossCell.Opacity = lost == 0 ? 0.45 : 1;
    }
}
