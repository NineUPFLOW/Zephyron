using Zephyron.Proxy;
using Zephyron.Supervisor;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>Подбор снимает мёртвый пин; пин сервиса под десинк и отказ сторожа не трогает.</summary>
public sealed class SiteFixerDeadPinTests : IDisposable
{
    private readonly string _hosts = Path.Combine(Path.GetTempPath(), "deadpin-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(_hosts)!, Path.GetFileName(_hosts) + "*"))
            File.Delete(file);
    }

    [Fact]
    public void PinIsFoundAndRemovedWithItsWwwPair()
    {
        File.WriteAllLines(_hosts, ["127.0.0.1 localhost", "31.25.239.132 vrv.co www.vrv.co # чужой сборник"]);

        Assert.Equal("31.25.239.132", SiteFixer.DeadPin("vrv.co", _hosts)?.Address);
        Assert.Null(SiteFixer.Unpin("vrv.co", _hosts));
        Assert.Null(SiteFixer.DeadPin("www.vrv.co", _hosts));
        Assert.Contains("localhost", File.ReadAllText(_hosts));
    }

    [Fact]
    public void ServicePinsAndVideoRefusalsStay()
    {
        File.WriteAllLines(_hosts,
        [
            $"104.18.1.1 example.com # {HostsEditor.DesyncMark}",
            $"0.0.0.0 rr1---sn-x.googlevideo.com # zy:via={VideoNodeGuard.Via}",
        ]);

        Assert.Null(SiteFixer.DeadPin("example.com", _hosts));
        Assert.Null(SiteFixer.DeadPin("rr1---sn-x.googlevideo.com", _hosts));
    }
}
