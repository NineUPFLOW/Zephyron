using Zephyron.Zapret;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>Десинк всегда покрывает api.cloudflareclient.com, когда WARP — путь туннеля.</summary>
public sealed class WarpApiDesyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"zephyron-warp-{Guid.NewGuid():N}");

    private ZapretPreset Load(string text)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "preset.txt");
        File.WriteAllText(path, text);
        return new PresetReader().Load(path);
    }

    private const string Plain = """
        #name=Проба
        --wf-tcp=80,443

        --new
        --name=Сайт
        --filter-tcp=80,443
        --hostlist-domains=example.com
        --out-range=-d8
        --lua-desync=multidisorder:pos=1,midsld

        --new
        --name=QUIC
        --filter-udp=443
        --hostlist-domains=example.com
        --lua-desync=fake:blob=quic_google
        """;

    private const string OwnWarp = """

        --new
        --name=Cloudflare WARP API
        --filter-tcp=443
        --hostlist-domains=api.cloudflareclient.com
        --out-range=-d8
        --lua-desync=fake:blob=tls_google
        """;

    [Fact]
    public void Off_leaves_the_line_unchanged()
    {
        var preset = Load(Plain);

        Assert.Equal(WinwsCommandLine.Build(preset), WinwsCommandLine.Build(preset, warpApi: false));
        Assert.DoesNotContain("--hostlist-domains=api.cloudflareclient.com", WinwsCommandLine.Build(preset));
    }

    [Fact]
    public void On_adds_a_tls_profile_with_the_preset_recipe_before_its_sections()
    {
        var line = WinwsCommandLine.Build(Load(Plain), warpApi: true).ToList();

        int name = line.IndexOf("--name=Zephyron: WARP API");
        Assert.True(name > 0);
        Assert.Equal("--new", line[name - 1]);
        Assert.Equal(
            ["--filter-tcp=443", "--hostlist-domains=api.cloudflareclient.com", "--out-range=-d8",
             "--lua-desync=multidisorder:pos=1,midsld", "--new"],
            line.Skip(name + 1).Take(5));
        Assert.True(name < line.IndexOf("--name=Сайт"));
    }

    [Fact]
    public void On_reuses_the_recipe_of_the_preset_own_warp_section()
    {
        var line = WinwsCommandLine.Build(Load(Plain + OwnWarp), warpApi: true).ToList();

        int name = line.IndexOf("--name=Zephyron: WARP API");
        Assert.Contains("--lua-desync=fake:blob=tls_google", line.Skip(name).Take(6));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
