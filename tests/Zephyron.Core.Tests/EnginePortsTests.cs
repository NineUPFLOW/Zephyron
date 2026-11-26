using System.Net.Sockets;
using Xunit;
using Zephyron.Proxy;

namespace Zephyron.Core.Tests;

/// <summary>РџРѕРґР±РѕСЂ Рё С‡С‚РµРЅРёРµ СЃР»СѓР¶РµР±РЅС‹С… РїРѕСЂС‚РѕРІ РґРІРёР¶РєР° (СЂРµР·РµСЂРІ РїРѕСЂС‚РѕРІ Hyper-V / WinNAT).</summary>
public sealed class EnginePortsTests
{
    private static string Missing() => Path.Combine(Path.GetTempPath(), $"zy-ports-{Guid.NewGuid():N}.json");

    [Fact]
    public void AccessDenied_picks_another_port()
    {
        int picked = EnginePorts.Pick(
            SingBoxOptions.DefaultHealthPort, Missing(),
            probe: p => p == SingBoxOptions.DefaultHealthPort ? SocketError.AccessDenied : null,
            allocate: () => 45123);

        Assert.Equal(45123, picked);
    }

    [Fact]
    public void Free_preferred_port_is_kept()
    {
        Assert.Equal(9090, EnginePorts.Pick(9090, Missing(), probe: _ => null, allocate: () => 45123));
    }

    [Fact]
    public void Port_in_use_by_a_foreign_program_picks_another_port()
    {
        int picked = EnginePorts.Pick(
            9090, Missing(),
            probe: p => p == 9090 ? SocketError.AddressAlreadyInUse : null,
            allocate: () => 45125);

        Assert.Equal(45125, picked);
    }

    [Fact]
    public void Port_held_by_own_engine_in_current_config_is_kept_but_access_denied_is_not()
    {
        var path = Missing();
        try
        {
            File.WriteAllText(path, """{"experimental":{"clash_api":{"external_controller":"127.0.0.1:9090"}}}""");

            Assert.Equal(9090, EnginePorts.Pick(9090, path,
                probe: _ => SocketError.AddressAlreadyInUse, allocate: () => 45126));
            Assert.Equal(45126, EnginePorts.Pick(9090, path,
                probe: p => p == 9090 ? SocketError.AccessDenied : null, allocate: () => 45126));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Avoided_ports_are_skipped()
    {
        int picked = EnginePorts.Pick(9090, Missing(), avoid: [9090, 45127], probe: _ => null, allocate: new Queue<int>([45127, 45128]).Dequeue);

        Assert.Equal(45128, picked);
    }

    [Fact]
    public void Ports_are_read_from_singbox_json_and_default_when_absent()
    {
        var ports = EnginePorts.Parse("""
            {"experimental":{"clash_api":{"external_controller":"127.0.0.1:45909"}},
             "inbounds":[{"tag":"tun-in"},{"tag":"health-in","listen_port":45910}]}
            """);

        Assert.Equal(45909, ports.ClashPort);
        Assert.Equal(45910, ports.HealthPort);

        var none = EnginePorts.Parse("{}");
        Assert.Equal(TunnelStatus.DefaultClashPort, none.ClashPort);
        Assert.Equal(SingBoxOptions.DefaultHealthPort, none.HealthPort);
        Assert.Equal(none, EnginePorts.Current(Missing()));
    }
}
