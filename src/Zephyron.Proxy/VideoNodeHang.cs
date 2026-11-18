using System.Net;
using System.Runtime.InteropServices;

namespace Zephyron.Proxy;

/// <summary>
/// Узлы видео, которые молчат на запросы самого плеера, — по счётчикам байт соединений браузера.
/// </summary>
/// <remarks>
/// <para>
/// Замер 19.11 у владельца (Билайн): кэш Google внутри сети провайдера
/// (<c>rr2---sn-8ph2xajvh-nufl</c>) ролики из кэша отдаёт за миллисекунды, а на остальные
/// запрос плеера уходит, и сервер не присылает ни байта; через 5–7 с плеер сдаётся, берёт
/// зарубежный узел — и через пару запросов снова пробует кэш. Каждая перемотка стояла ~8 с.
/// <see cref="VideoNodeGuard.IsFilteredAsync"/> такого не видит: на <c>generate_204</c> кэш отвечает.
/// </para>
/// <para>
/// Сброс зависшего соединения не помогает (проверено): браузер тут же повторяет запрос
/// на новом соединении к тому же узлу. Помогает отказ узлу на время: перемотки встали
/// на 0,6–1,9 с. Но кэш быстрее зарубежных узлов для роликов, которые в нём есть
/// (4K: 67 против 10 Мбит/с), поэтому отказ — короткий (<see cref="VideoNodeGuard.HangHold"/>)
/// и только когда другой узел в это же время отдаёт видео.
/// </para>
/// </remarks>
public sealed class HangTracker
{
    /// <summary>Сколько байт от браузера считать запросом, а не служебным пакетом TLS.</summary>
    public const ulong RequestBytes = 200;

    /// <summary>Здоровый узел отвечает за десятки миллисекунд, долгая выдача из кэша — за 4 с; плеер ждёт 5–7 с.</summary>
    public static readonly TimeSpan Silence = TimeSpan.FromSeconds(2.5);

    /// <summary>Отказ — после двух зависаний узла за это время: одно бывает и у живого.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    /// <summary>Другой узел отдавал видео не раньше этого — значит, плееру есть куда уйти.</summary>
    public static readonly TimeSpan AlternativeRecent = TimeSpan.FromSeconds(30);

    /// <summary>Сколько байт за замер считать выдачей видео, а не ответом на проверку.</summary>
    public const ulong VideoBytes = 50_000;

    /// <summary>
    /// Первые секунды отказов нет: перезапуск движков рвёт соединения на полпути,
    /// и они молчат не по вине узла (19.11: отказ узлу через 5 с после запуска).
    /// </summary>
    public static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(30);

    private DateTime? _start;

    public readonly record struct Key(uint Local, uint LocalPort, uint Remote, uint RemotePort);

    public readonly record struct Sample(Key Key, string Host, ulong Out, ulong In);

    private sealed class Connection(string host, ulong sent, ulong received)
    {
        public string Host { get; } = host;
        public ulong Out { get; set; } = sent;
        public ulong In { get; set; } = received;
        public DateTime? WaitingSince { get; set; }
        public bool Counted { get; set; }
    }

    private readonly Dictionary<Key, Connection> _connections = [];
    private readonly Dictionary<string, List<DateTime>> _hangs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _video = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Один замер всех соединений к узлам видео; возвращает узлы, которым пора отказать.</summary>
    public IReadOnlyList<string> Observe(IReadOnlyCollection<Sample> samples, DateTime now)
    {
        var seen = new HashSet<Key>();
        _start ??= now;

        foreach (var sample in samples)
        {
            seen.Add(sample.Key);

            // Первое знакомство — только точка отсчёта: соединение могло простаивать
            // между запросами, и его прошлые байты ничего не говорят о молчании.
            if (!_connections.TryGetValue(sample.Key, out var connection))
            {
                _connections[sample.Key] = new Connection(sample.Host, sample.Out, sample.In);
                continue;
            }

            bool sent = sample.Out >= connection.Out + RequestBytes;
            bool received = sample.In > connection.In;

            if (received)
            {
                if (sample.In - connection.In >= VideoBytes)
                    _video[connection.Host] = now;

                connection.WaitingSince = null;
                connection.Counted = false;
            }
            // Запрос и ответ в одном замере — ответ пришёл, ждать нечего.
            else if (sent)
            {
                // Новый запрос после уже засчитанного молчания — новое ожидание.
                if (connection.Counted)
                {
                    connection.WaitingSince = null;
                    connection.Counted = false;
                }

                connection.WaitingSince ??= now;
            }

            connection.Out = Math.Max(connection.Out, sample.Out);
            connection.In = Math.Max(connection.In, sample.In);

            if (connection.WaitingSince is { } since && !connection.Counted && now - since >= Silence)
            {
                connection.Counted = true;

                if (!_hangs.TryGetValue(connection.Host, out var list))
                    _hangs[connection.Host] = list = [];

                list.Add(now);
            }
        }

        foreach (var key in _connections.Keys.Where(k => !seen.Contains(k)).ToList())
            _connections.Remove(key);

        var due = new List<string>();

        foreach (var (host, list) in _hangs)
        {
            list.RemoveAll(t => now - t > Window);

            bool alternative = _video.Any(p => !p.Key.Equals(host, StringComparison.OrdinalIgnoreCase) && now - p.Value <= AlternativeRecent);

            if (list.Count >= 2 && alternative && now - _start >= WarmUp)
            {
                due.Add(host);
                list.Clear();
            }
        }

        return due;
    }
}

/// <summary>Счётчики байт TCP-соединений Windows (EStats) — нужны права администратора.</summary>
public static class TcpBytes
{
    private const int Established = 5;
    private const int Ipv4 = 2;
    private const int OwnerPidAll = 5;
    private const int DataStats = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct OwnerRow { public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, Pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Row { public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataRw { public byte EnableCollection; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataRod
    {
        public ulong DataBytesOut, DataSegsOut, DataBytesIn, DataSegsIn, SegsOut, SegsIn;
        public uint SoftErrors, SoftErrorReason, SndUna, SndNxt, SndMax;
        public ulong ThruBytesAcked;
        public uint RcvNxt;
        public ulong ThruBytesReceived;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int kind, uint reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint SetPerTcpConnectionEStats(ref Row row, int type, ref DataRw rw, uint version, uint size, uint offset);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetPerTcpConnectionEStats(ref Row row, int type, IntPtr rw, uint rwVersion, uint rwSize,
        IntPtr ros, uint rosVersion, uint rosSize, out DataRod rod, uint rodVersion, uint rodSize);

    private static ushort Port(uint raw) => (ushort)(((raw & 0xff) << 8) | ((raw >> 8) & 0xff));

    /// <summary>Установленные соединения на порт 443 к этим адресам — с байтами в обе стороны.</summary>
    /// <param name="hosts">Адрес (как в таблице TCP) → имя узла.</param>
    public static List<HangTracker.Sample> Read(IReadOnlyDictionary<uint, string> hosts, ISet<HangTracker.Key> enabled)
    {
        var result = new List<HangTracker.Sample>();

        if (hosts.Count == 0)
            return result;

        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, Ipv4, OwnerPidAll, 0);

        if (size <= 0)
            return result;

        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, Ipv4, OwnerPidAll, 0) != 0)
                return result;

            int count = Marshal.ReadInt32(buffer);
            int width = Marshal.SizeOf<OwnerRow>();
            var live = new HashSet<HangTracker.Key>();

            for (int i = 0; i < count; i++)
            {
                var owner = Marshal.PtrToStructure<OwnerRow>(buffer + 4 + i * width);

                if (owner.State != Established || Port(owner.RemotePort) != 443 || !hosts.TryGetValue(owner.RemoteAddr, out var host))
                    continue;

                var row = new Row { State = owner.State, LocalAddr = owner.LocalAddr, LocalPort = owner.LocalPort, RemoteAddr = owner.RemoteAddr, RemotePort = owner.RemotePort };
                var key = new HangTracker.Key(owner.LocalAddr, owner.LocalPort, owner.RemoteAddr, owner.RemotePort);
                live.Add(key);

                // Счёт включается на соединение один раз; байты он считает с начала соединения.
                if (enabled.Add(key))
                {
                    var rw = new DataRw { EnableCollection = 1 };
                    SetPerTcpConnectionEStats(ref row, DataStats, ref rw, 0, (uint)Marshal.SizeOf<DataRw>(), 0);
                }

                if (GetPerTcpConnectionEStats(ref row, DataStats, IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0,
                        out var rod, 0, (uint)Marshal.SizeOf<DataRod>()) == 0)
                    result.Add(new HangTracker.Sample(key, host, rod.DataBytesOut, rod.DataBytesIn));
            }

            enabled.IntersectWith(live);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    /// <summary>Адрес IPv4 в том виде, в каком он стоит в таблице TCP.</summary>
    public static uint Raw(IPAddress address) => BitConverter.ToUInt32(address.GetAddressBytes());
}
