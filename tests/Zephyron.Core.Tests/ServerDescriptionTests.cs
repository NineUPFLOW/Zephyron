using System.Text;
using Xunit;
using Zephyron.Subscriptions;

namespace Zephyron.Core.Tests;

/// <summary>Описание сервера от панели в имени и flow VLESS для sing-box.</summary>
public sealed class ServerDescriptionTests
{
    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Tail_is_cut_from_the_name_and_decoded()
    {
        var (name, description) = ServerDescription.Split("🇷🇺 Обход 💫?serverDescription=" + Base64("Для LTE (белые списки)"));

        Assert.Equal("🇷🇺 Обход 💫", name);
        Assert.Equal("Для LTE (белые списки)", description);
    }

    [Fact]
    public void Name_without_tail_stays_as_is()
    {
        Assert.Equal(("Германия", (string?)null), ServerDescription.Split("Германия"));

        var bare = "?serverDescription=" + Base64("x");
        Assert.Equal(bare, ServerDescription.Split(bare).Name);
    }

    [Fact]
    public void Saved_tags_lose_the_tail_on_read()
    {
        var tail = "?serverDescription=" + Base64("Для LTE (белые списки)");
        var path = Path.Combine(Path.GetTempPath(), $"zy-settings-{Guid.NewGuid():N}.json");

        try
        {
            new AppSettings
            {
                PreferredServer = "Обход 💫" + tail,
                AutoPickExcluded = ["Обход №3" + tail, "Латвия"],
                Pools = [new ServerPool("ru", "Россия", ["Обход №1" + tail], Preferred: "Обход №1" + tail)],
            }.Save(path);

            var read = AppSettings.Load(path);

            Assert.Equal("Обход 💫", read.PreferredServer);
            Assert.Equal(["Обход №3", "Латвия"], read.AutoPickExcluded);
            Assert.Equal(["Обход №1"], read.Pools[0].Servers);
            Assert.Equal("Обход №1", read.Pools[0].Preferred);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("xtls-rprx-vision", "xtls-rprx-vision")]
    [InlineData("XTLS-RPRX-VISION", "xtls-rprx-vision")]
    [InlineData("xtls-rprx-vision-udp443", "xtls-rprx-vision")]
    [InlineData("xtls-rprx-direct", null)]
    [InlineData("none", null)]
    public void Vless_flow_is_mapped_the_way_singbox_accepts(string? flow, string? expected) =>
        Assert.Equal(expected, VlessFlows.ForSingBox(flow));
}
