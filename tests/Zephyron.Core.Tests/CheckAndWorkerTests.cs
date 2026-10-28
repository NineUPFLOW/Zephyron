using System.Net;
using Xunit;
using Zephyron.Proxy;

namespace Zephyron.Core.Tests;

/// <summary>Воркер TG Proxy, отказ по стране и домашние адреса в проверке блокировок.</summary>
public class CheckAndWorkerTests
{
    [Theory]
    [InlineData("https://tg.me.workers.dev/", "tg.me.workers.dev")]
    [InlineData("  TG.Me.Workers.Dev ", "tg.me.workers.dev")]
    [InlineData("tg.me.workers.dev --dc-ip 1.2.3.4", null)]
    [InlineData("localhost", null)]
    [InlineData("", null)]
    public void WorkerHostTakesOnlyAName(string text, string? expected) =>
        Assert.Equal(expected, TgWsProxy.WorkerHost(text));

    [Fact]
    public void WorkerGoesToArguments()
    {
        var arguments = TgWsProxy.Arguments(1443, new TgWsProxyRoutes(Worker: "tg.me.workers.dev"));
        int at = arguments.ToList().IndexOf("--cf-worker-domain");
        Assert.True(at >= 0);
        Assert.Equal("tg.me.workers.dev", arguments[at + 1]);
    }

    [Theory]
    [InlineData("<h1>Not available in your country</h1>", true)]
    [InlineData("Сервис недоступен в вашем регионе", true)]
    [InlineData("<h1>403 Forbidden</h1>", false)]
    public void RefusalNamesRegion(string page, bool named) =>
        Assert.Equal(named, BlockCheck.NamesRegion(page));

    [Theory]
    [InlineData("192.168.1.10", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("93.184.216.34", false)]
    public void HomeAddresses(string address, bool home) =>
        Assert.Equal(home, BlockCheck.IsHome(IPAddress.Parse(address)));
}
