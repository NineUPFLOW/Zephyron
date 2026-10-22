using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
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

    /// <summary>Сколько секунд приёма в полоске.</summary>
    private const int SparkBars = 48;

    private const string PingTarget = "1.1.1.1";

    /// <summary>Имя адаптера туннеля — то же, что даёт ему конфиг движка.</summary>
    private static readonly string TunName = new Proxy.SingBoxOptions().TunInterfaceName;

    private readonly DispatcherTimer _net = new() { Interval = NetInterval };
    private readonly Queue<bool> _pings = new();
    private readonly Queue<double> _down = new();

    private (long Received, long Sent, DateTime At)? _lastBytes;
    private DateTime _lastPing = DateTime.MinValue;
    private bool _netBusy;
    private bool _pingBusy;

    /// <summary>
    /// Меряет, пока обход работает, и стоит, пока нет.
    /// </summary>
    /// <remarks>
    /// Владелец 22.10: метрика — часть запуска, а не всегдашний прибор.
    /// Остановленная строка показывает нули и прочерки, а не последнее
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

    /// <summary>Строка без замеров: нули скорости, прочерки задержки и потерь, пустая полоска.</summary>
    private void ResetNet()
    {
        _lastBytes = null;
        _lastPing = DateTime.MinValue;
        _pings.Clear();
        _down.Clear();

        NetDown.Text = "0.0";
        NetUp.Text = "0.0";
        NetPing.Text = "—";
        NetPingUnit.Text = string.Empty;
        NetLoss.Text = "—";
        NetLossUnit.Text = string.Empty;

        foreach (var cell in new FrameworkElement[] { NetDownCell, NetUpCell, NetPingCell, NetLossCell })
            cell.Opacity = 0.45;

        ShowSpark();
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

        NetDown.Text = Rate(down);
        NetUp.Text = Rate(up);

        // Тихая сеть — тише и строка: меньше 10 кбит/с читается как «ничего».
        NetDownCell.Opacity = down < 0.01 ? 0.45 : 1;
        NetUpCell.Opacity = up < 0.01 ? 0.45 : 1;

        _down.Enqueue(down);

        while (_down.Count > SparkBars)
            _down.Dequeue();

        ShowSpark();
    }

    /// <summary>«0.0», «8.4», «312» — три значащих знака, без лишних нулей у больших.</summary>
    internal static string Rate(double mbps) =>
        mbps >= 100 ? mbps.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
            : mbps.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Полоски приёма: высота — доля от наибольшего за минуту.</summary>
    private void ShowSpark()
    {
        if (NetSpark.Children.Count != SparkBars)
        {
            NetSpark.Children.Clear();
            NetSpark.ColumnDefinitions.Clear();

            for (int i = 0; i < SparkBars; i++)
            {
                NetSpark.ColumnDefinitions.Add(new ColumnDefinition());

                var bar = new Rectangle
                {
                    RadiusX = 1,
                    RadiusY = 1,
                    Margin = new Thickness(0.5, 0, 0.5, 0),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Opacity = 0.55,
                    Height = 1,
                };
                bar.SetResourceReference(Shape.FillProperty, "Accent");

                Grid.SetColumn(bar, i);
                NetSpark.Children.Add(bar);
            }
        }

        var values = _down.ToArray();
        double peak = Math.Max(0.05, values.DefaultIfEmpty(0).Max());
        int offset = SparkBars - values.Length;

        for (int i = 0; i < SparkBars; i++)
        {
            var bar = (Rectangle)NetSpark.Children[i];
            double value = i >= offset ? values[i - offset] : 0;

            bar.Height = Math.Max(1, Math.Round(value / peak * 18));
            bar.SetResourceReference(Shape.FillProperty, value < 0.01 ? "Faint" : "Accent");
        }
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

        while (_pings.Count > PingWindow)
            _pings.Dequeue();

        NetPing.Text = ms is { } value ? value.ToString() : "—";
        NetPingUnit.Text = ms is null ? string.Empty : "мс";
        NetPingCell.Opacity = ms is null ? 0.45 : 1;

        int lost = _pings.Count(ok => !ok);
        NetLoss.Text = $"{100.0 * lost / _pings.Count:0}";
        NetLossUnit.Text = "%";
        NetLossCell.Opacity = lost == 0 ? 0.45 : 1;
    }
}
