using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Zephyron.Supervisor;

/// <summary>
/// Kill switch режима «Туннель»: пока работает надзор, с физических адаптеров
/// не уходит ничего, кроме движка туннеля, самой программы и локальной сети.
/// </summary>
/// <remarks>
/// <para>
/// Фильтры Windows (WFP) в динамическом сеансе: они живут, пока открыт сеанс,
/// и Windows снимает их сама, когда процесс надзора уходит — остановкой,
/// выходом или падением. Поэтому упавший или перезапускающийся туннель
/// трафик мимо себя не выпускает, а программа, которой уже нет, сеть не держит.
/// </para>
/// <para>
/// Блокируется выход через физические адаптеры, а не «всё, кроме TUN»: адаптер
/// туннеля появляется и пропадает с каждым запуском движка, а физические
/// известны заранее. Через TUN трафик идёт как шёл.
/// </para>
/// </remarks>
public sealed class KillSwitch : IDisposable
{
    private nint _engine;
    private IReadOnlyList<string> _allowed = [];
    private readonly object _gate = new();

    private KillSwitch(nint engine) => _engine = engine;

    /// <summary>
    /// Ставит фильтры; <c>null</c> и причина — не вышло (нет прав, WFP отказал).
    /// </summary>
    /// <remarks>
    /// Сеть меняется — фильтры ставятся заново: адаптер, появившийся после
    /// запуска (второй Wi-Fi, телефон по USB), иначе остался бы открытым.
    /// Новые ставятся раньше, чем снимаются старые, — окна без фильтров нет.
    /// </remarks>
    /// <param name="allowed">Программы, которым выход мимо туннеля разрешён: движок и сама программа.</param>
    public static KillSwitch? Open(IReadOnlyList<string> allowed, out string? problem)
    {
        var opened = Build(allowed, out problem);

        if (opened is not null)
        {
            opened._allowed = allowed;
            NetworkChange.NetworkAddressChanged += opened.OnNetworkChanged;
        }

        return opened;
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_engine == 0 || Build(_allowed, out _) is not { } fresh)
                return;

            var old = _engine;
            _engine = fresh._engine;
            fresh._engine = 0;
            FwpmEngineClose0(old);
        }
    }

    private static KillSwitch? Build(IReadOnlyList<string> allowed, out string? problem)
    {
        problem = null;
        var session = new FWPM_SESSION0 { flags = FWPM_SESSION_FLAG_DYNAMIC };
        var name = Marshal.StringToHGlobalUni("Zephyron kill switch");
        session.displayData.name = name;

        var pinned = new List<nint> { name };

        try
        {
            uint error = FwpmEngineOpen0(null, RPC_C_AUTHN_WINNT, 0, ref session, out var engine);

            if (error != 0)
            {
                problem = $"фильтры Windows не открылись (0x{error:X8})";
                return null;
            }

            var result = new KillSwitch(engine);

            try
            {
                Check(FwpmTransactionBegin0(engine, 0), "начать");
                result.AddFilters(allowed, name, pinned);
                Check(FwpmTransactionCommit0(engine), "записать");
                return result;
            }
            catch (Exception ex)
            {
                FwpmTransactionAbort0(engine);
                result.Dispose();
                problem = ex.Message;
                return null;
            }
        }
        finally
        {
            foreach (var p in pinned)
                Marshal.FreeHGlobal(p);
        }
    }

    /// <summary>
    /// Адаптеры, через которые выход закрыт: с основным шлюзом, кроме петли и туннельных.
    /// </summary>
    /// <remarks>
    /// Без шлюза адаптер в интернет не ведёт: это виртуальные сети — Radmin,
    /// Hamachi, ZeroTier, Hyper-V, WSL. Их закрывать незачем, а игра по локалке
    /// перестала бы работать и при живом туннеле.
    /// </remarks>
    public static IReadOnlyList<ulong> PhysicalAdapters()
    {
        var result = new List<ulong>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                || nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase)
                || nic.Name.StartsWith("zephyron", StringComparison.OrdinalIgnoreCase)
                || !HasGateway(nic)
                || !Guid.TryParse(nic.Id, out var id)
                || ConvertInterfaceGuidToLuid(ref id, out var luid) != 0)
                continue;

            result.Add(luid);
        }

        return result;
    }

    private static bool HasGateway(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GatewayAddresses.Any(g =>
                !g.Address.Equals(System.Net.IPAddress.Any) && !g.Address.Equals(System.Net.IPAddress.IPv6Any));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    /// <summary>Локальная сеть: роутер, принтеры, DHCP и мультикаст не закрываются.</summary>
    private static readonly (uint Address, uint Mask)[] Lan =
    [
        (0x0A000000, 0xFF000000), // 10/8
        (0xAC100000, 0xFFF00000), // 172.16/12
        (0xC0A80000, 0xFFFF0000), // 192.168/16
        (0xA9FE0000, 0xFFFF0000), // 169.254/16
        (0x64400000, 0xFFC00000), // 100.64/10 — шлюз и DHCP у операторов с CGNAT
        (0x19000000, 0xFF000000), // 25/8 — Hamachi
        (0x1A000000, 0xFF000000), // 26/8 — Radmin VPN
        (0xE0000000, 0xF0000000), // 224/4
        (0xFFFFFFFF, 0xFFFFFFFF), // широковещание
    ];

    /// <summary>Локальная сеть IPv6: link-local и мультикаст (обнаружение соседей, принтеры).</summary>
    private static readonly (System.Net.IPAddress Prefix, byte Length)[] LanV6 =
    [
        (System.Net.IPAddress.Parse("fe80::"), 10),
        (System.Net.IPAddress.Parse("ff00::"), 8),
    ];

    private void AddFilters(IReadOnlyList<string> allowed, nint name, List<nint> pinned)
    {
        var subLayer = new FWPM_SUBLAYER0 { subLayerKey = Guid.NewGuid(), weight = 0xFFFF };
        subLayer.displayData.name = name;
        Check(FwpmSubLayerAdd0(_engine, ref subLayer, 0), "завести подслой");

        // Один снимок адаптеров на оба семейства: иначе списки V4 и V6 могли бы разойтись.
        var adapters = PhysicalAdapters();

        foreach (var layer in new[] { FWPM_LAYER_ALE_AUTH_CONNECT_V4, FWPM_LAYER_ALE_AUTH_CONNECT_V6 })
        {
            // Разрешения — тяжелее запрета: в своём подслое решает самый тяжёлый совпавший.
            foreach (var path in allowed.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Check(FwpmGetAppIdFromFileName0(path, out var blob), "узнать программу " + Path.GetFileName(path));

                try
                {
                    Add(layer, subLayer.subLayerKey, name, FWP_ACTION_PERMIT, 15,
                        Condition(FWPM_CONDITION_ALE_APP_ID, FWP_BYTE_BLOB_TYPE, blob));
                }
                finally
                {
                    FwpmFreeMemory0(ref blob);
                }
            }

            if (layer == FWPM_LAYER_ALE_AUTH_CONNECT_V4)
            {
                foreach (var (address, mask) in Lan)
                {
                    var value = Marshal.AllocHGlobal(8);
                    pinned.Add(value);
                    Marshal.WriteInt32(value, 0, unchecked((int)address));
                    Marshal.WriteInt32(value, 4, unchecked((int)mask));
                    Add(layer, subLayer.subLayerKey, name, FWP_ACTION_PERMIT, 15,
                        Condition(FWPM_CONDITION_IP_REMOTE_ADDRESS, FWP_V4_ADDR_MASK, value));
                }
            }
            else
            {
                // FWP_V6_ADDR_AND_MASK: 16 байт адреса и длина префикса.
                foreach (var (prefix, length) in LanV6)
                {
                    var value = Marshal.AllocHGlobal(17);
                    pinned.Add(value);
                    var bytes = prefix.GetAddressBytes();
                    Marshal.Copy(bytes, 0, value, 16);
                    Marshal.WriteByte(value, 16, length);
                    Add(layer, subLayer.subLayerKey, name, FWP_ACTION_PERMIT, 15,
                        Condition(FWPM_CONDITION_IP_REMOTE_ADDRESS, FWP_V6_ADDR_MASK, value));
                }
            }

            foreach (var luid in adapters)
            {
                var value = Marshal.AllocHGlobal(8);
                pinned.Add(value);
                Marshal.WriteInt64(value, unchecked((long)luid));
                Add(layer, subLayer.subLayerKey, name, FWP_ACTION_BLOCK, 0,
                    Condition(FWPM_CONDITION_IP_LOCAL_INTERFACE, FWP_UINT64, value));
            }
        }
    }

    private static FWPM_FILTER_CONDITION0 Condition(Guid field, uint type, nint value) =>
        new() { fieldKey = field, matchType = FWP_MATCH_EQUAL, conditionType = type, conditionValue = value };

    private void Add(Guid layer, Guid subLayer, nint name, uint action, byte weight, FWPM_FILTER_CONDITION0 condition)
    {
        var conditions = Marshal.AllocHGlobal(Marshal.SizeOf<FWPM_FILTER_CONDITION0>());

        try
        {
            Marshal.StructureToPtr(condition, conditions, false);

            var filter = new FWPM_FILTER0
            {
                filterKey = Guid.NewGuid(),
                displayName = name,
                layerKey = layer,
                subLayerKey = subLayer,
                weightType = FWP_UINT8,
                weightValue = weight,
                numFilterConditions = 1,
                filterCondition = conditions,
                actionType = action,
            };

            Check(FwpmFilterAdd0(_engine, ref filter, 0, out _), "добавить фильтр");
        }
        finally
        {
            Marshal.FreeHGlobal(conditions);
        }
    }

    private static void Check(uint error, string what)
    {
        if (error != 0)
            throw new InvalidOperationException($"фильтры Windows: не удалось {what} (0x{error:X8})");
    }

    /// <summary>Закрывает сеанс — Windows снимает все его фильтры разом.</summary>
    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;

        lock (_gate)
        {
            if (_engine != 0)
            {
                FwpmEngineClose0(_engine);
                _engine = 0;
            }
        }
    }

    private const uint RPC_C_AUTHN_WINNT = 10;
    private const uint FWPM_SESSION_FLAG_DYNAMIC = 1;
    private const uint FWP_MATCH_EQUAL = 0;
    private const uint FWP_UINT8 = 1;
    private const uint FWP_UINT64 = 4;
    private const uint FWP_BYTE_BLOB_TYPE = 12;
    private const uint FWP_V4_ADDR_MASK = 0x100;
    private const uint FWP_V6_ADDR_MASK = 0x101;
    private const uint FWP_ACTION_BLOCK = 0x1001;
    private const uint FWP_ACTION_PERMIT = 0x1002;

    private static readonly Guid FWPM_LAYER_ALE_AUTH_CONNECT_V4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    private static readonly Guid FWPM_LAYER_ALE_AUTH_CONNECT_V6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    private static readonly Guid FWPM_CONDITION_ALE_APP_ID = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    private static readonly Guid FWPM_CONDITION_IP_LOCAL_INTERFACE = new("4cd62a49-59c3-4969-b7f3-bda5d32890a4");
    private static readonly Guid FWPM_CONDITION_IP_REMOTE_ADDRESS = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");

    [StructLayout(LayoutKind.Sequential)]
    private struct FWPM_DISPLAY_DATA0
    {
        public nint name;
        public nint description;
    }

    // Раскладки — для x64, со смещениями из fwpmtypes.h: программа собирается только под win-x64.
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct FWPM_SESSION0
    {
        [FieldOffset(16)] public FWPM_DISPLAY_DATA0 displayData;
        [FieldOffset(32)] public uint flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct FWPM_SUBLAYER0
    {
        [FieldOffset(0)] public Guid subLayerKey;
        [FieldOffset(16)] public FWPM_DISPLAY_DATA0 displayData;
        [FieldOffset(64)] public ushort weight;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct FWPM_FILTER_CONDITION0
    {
        [FieldOffset(0)] public Guid fieldKey;
        [FieldOffset(16)] public uint matchType;
        [FieldOffset(24)] public uint conditionType;
        [FieldOffset(32)] public nint conditionValue;
    }

    [StructLayout(LayoutKind.Explicit, Size = 200)]
    private struct FWPM_FILTER0
    {
        [FieldOffset(0)] public Guid filterKey;
        [FieldOffset(16)] public nint displayName;
        [FieldOffset(64)] public Guid layerKey;
        [FieldOffset(80)] public Guid subLayerKey;
        [FieldOffset(96)] public uint weightType;
        [FieldOffset(104)] public byte weightValue;
        [FieldOffset(112)] public uint numFilterConditions;
        [FieldOffset(120)] public nint filterCondition;
        [FieldOffset(128)] public uint actionType;
    }

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    private static extern uint FwpmEngineOpen0(string? serverName, uint authnService, nint authIdentity, ref FWPM_SESSION0 session, out nint engineHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmEngineClose0(nint engineHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmTransactionBegin0(nint engineHandle, uint flags);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmTransactionCommit0(nint engineHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmTransactionAbort0(nint engineHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmSubLayerAdd0(nint engineHandle, ref FWPM_SUBLAYER0 subLayer, nint sd);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmFilterAdd0(nint engineHandle, ref FWPM_FILTER0 filter, nint sd, out ulong id);

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    private static extern uint FwpmGetAppIdFromFileName0(string fileName, out nint appId);

    [DllImport("fwpuclnt.dll")]
    private static extern void FwpmFreeMemory0(ref nint p);

    [DllImport("iphlpapi.dll")]
    private static extern uint ConvertInterfaceGuidToLuid(ref Guid guid, out ulong luid);
}
