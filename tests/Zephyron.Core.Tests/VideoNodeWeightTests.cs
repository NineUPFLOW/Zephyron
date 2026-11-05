using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;
using Zephyron.Proxy;
using Zephyron.Zapret;

namespace Zephyron.Core.Tests;

public class VideoNodeWeightTests
{
    [Theory]
    [InlineData("rr5---sn-8ph2xajvh-nufs.googlevideo.com", true)]
    [InlineData("redirector.googlevideo.com", true)]
    [InlineData("googlevideo.com", false)]
    [InlineData("rr5---sn-x.googlevideo.com.evil.test", false)]
    [InlineData("www.youtube.com", false)]
    public void Node_name_is_recognised(string host, bool expected) =>
        Assert.Equal(expected, BlockCheck.IsVideoNode(host));

    private static readonly byte[] Answer = Encoding.ASCII.GetBytes(
        "HTTP/1.1 404 Not Found\r\nContent-Length: 1561\r\n\r\n" + new string('x', 1561));

    /// <summary>Настоящее соединение с сервером, отвечающим на <paramref name="answers"/> запросов.</summary>
    private static async Task<ProbeOutcome> WeighAsync(int answers, bool closeAfter)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await listener.AcceptTcpClientAsync();

        var server = Task.Run(async () =>
        {
            var stream = accepted.GetStream();
            var buffer = new byte[4096];

            for (int i = 0; i < answers; i++)
            {
                // Порциями: заголовок рвётся посередине, как бывает в сети.
                if (await stream.ReadAsync(buffer) == 0)
                    return;

                await stream.WriteAsync(Answer.AsMemory(0, 20));
                await Task.Delay(5);
                await stream.WriteAsync(Answer.AsMemory(20));
            }

            // Запрос дочитывается: закрытие с непрочитанным дало бы RST, а не тихий конец.
            if (closeAfter)
            {
                await stream.ReadAsync(buffer);
                accepted.Close();
            }
        });

        var outcome = await BlockCheck.WeighNodeAsync(client.GetStream(), "node", null, CancellationToken.None);

        // Иначе сервер ждал бы запросов, которых проба, набрав своё, уже не пошлёт.
        client.Close();
        await server;

        return outcome;
    }

    [Fact]
    public async Task Answers_add_up_on_one_connection()
    {
        var outcome = await WeighAsync(50, closeAfter: false);

        Assert.True(outcome.Ok);
        Assert.True(outcome.Received >= 64 * 1024);
    }

    [Fact]
    public async Task Silence_past_the_threshold_is_a_freeze()
    {
        var outcome = await WeighAsync(10, closeAfter: false);

        Assert.False(outcome.Ok);
        Assert.True(outcome.Started);
        Assert.Equal(10 * Answer.Length, outcome.Received);
        Assert.Contains("замер", outcome.Detail);
    }

    [Fact]
    public async Task Closed_connection_is_not_a_block()
    {
        var outcome = await WeighAsync(2, closeAfter: true);

        Assert.True(outcome.Ok);
        Assert.Contains("закрыл", outcome.Detail);
    }
}

public class NoYouTubeQuicTests
{
    private static ZapretPreset Preset(params string[] section) => new()
    {
        Name = "t",
        FilePath = "t.txt",
        GlobalArguments = ["--wf-tcp-out=443"],
        Sections = [new ZapretSection { Name = "s", HostListPaths = [], InlineDomains = [], IpSetPaths = [], DesyncRecipes = [], RawArguments = section }],
    };

    [Fact]
    public void Quic_of_youtube_is_dropped_before_the_preset()
    {
        var line = WinwsCommandLine.Build(Preset("--name=gv", "--filter-tcp=443", "--hostlist=lists/googlevideo.txt", "--lua-desync=pass"));

        Assert.True(line.ToList().IndexOf("--lua-desync=drop") < line.ToList().IndexOf("--name=gv"));
        Assert.Contains("--filter-l7=quic", line);
    }

    [Fact]
    public void Video_recipe_is_repeated_by_address_as_the_last_profile()
    {
        var line = WinwsCommandLine.Build(Preset("--name=gv", "--filter-tcp=443", "--hostlist=lists/googlevideo.txt", "--lua-desync=split")).ToList();
        int by = line.IndexOf("--ipset=lists/ipset-googlevideo.txt");

        Assert.True(by > line.IndexOf("--name=gv"));
        Assert.Equal(["--filter-tcp=443", "--lua-desync=split"], line.Skip(by + 1));
    }

    [Fact]
    public void Broken_header_protection_key_is_refused()
    {
        Assert.Throws<FormatException>(() => Zephyron.Subscriptions.AmneziaSettings.From(k => k == "HeaderProtectionKey" ? "abc" : null));
    }

    [Fact]
    public void Preset_without_youtube_is_left_alone()
    {
        var line = WinwsCommandLine.Build(Preset("--name=x", "--filter-tcp=443", "--lua-desync=pass"));

        Assert.DoesNotContain("--lua-desync=drop", line);
    }

    [Fact]
    public void Shield_stays_ahead_of_the_drop()
    {
        var exclude = Path.GetTempFileName();

        try
        {
            File.WriteAllLines(exclude, ["googlevideo.com"]);

            var line = WinwsCommandLine.Build(Preset("--name=gv", "--hostlist=lists/googlevideo.txt", "--lua-desync=pass"), exclude, own: []).ToList();

            Assert.True(line.IndexOf("--lua-desync=pass") < line.IndexOf("--lua-desync=drop"));
        }
        finally
        {
            File.Delete(exclude);
        }
    }
}
