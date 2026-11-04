using Zephyron.Core;
using Zephyron.Zapret;
using Xunit;

namespace Zephyron.Core.Tests;

public sealed class FakeChoiceTests
{
    private static readonly string[] Line =
    [
        "--blob=tls_google:@bin/tls_google.bin",
        "--name=discord.media",
        "--filter-udp=50000-50100",
        "--lua-desync=fake:blob=quic_google:repeats=6",
        "--lua-desync=fake:blob=fake_default_udp",
        "--new",
        "--name=youtube",
        "--lua-desync=fake:blob=tls_google:repeats=4",
    ];

    [Fact]
    public void Apply_swaps_only_the_chosen_slot_and_declares_the_file_once()
    {
        var args = Line.ToList();
        var swaps = new[]
        {
            new FakeSwap("p", "discord.media", 0, "stun2.bin"),
            new FakeSwap("p", "youtube", 0, "stun2.bin"),
            new FakeSwap("other", "youtube", 0, "x.bin"),
        };

        Assert.Equal(2, FakeChoice.Apply(args, "P", swaps));
        Assert.Equal("--blob=zy_fake_stun2:@bin/stun2.bin", args[0]);
        Assert.Single(args, a => a.StartsWith("--blob=zy_fake_stun2", StringComparison.Ordinal));
        Assert.Contains("--lua-desync=fake:blob=zy_fake_stun2:repeats=6", args);
        Assert.Contains("--lua-desync=fake:blob=fake_default_udp", args);
    }

    [Fact]
    public void Built_in_blob_needs_no_declaration_and_no_swaps_changes_nothing()
    {
        var args = Line.ToList();
        Assert.Equal(1, FakeChoice.Apply(args, "p", [new FakeSwap("p", "youtube", 0, "fake_default_tls")]));
        Assert.Equal(Line.Length, args.Count);
        Assert.Equal(0, FakeChoice.Apply(Line.ToList(), "p", []));
        Assert.Equal(0, FakeChoice.Apply(Line.ToList(), "p", [new FakeSwap("p", "gone", 0, "a.bin")]));
        Assert.Equal(0, FakeChoice.Apply(Line.ToList(), "p", [new FakeSwap("p", "youtube", 0, "../out.bin")]));
        Assert.Equal(0, FakeChoice.Apply(Line.ToList(), "p", [new FakeSwap("p", "youtube", 0, "not-a-blob.exe")]));
    }

    [Fact]
    public void Search_matches_name_lists_and_filters()
    {
        var slots = new[]
        {
            new FakeSlot("discord.media", 0, "fake:blob=a", "a", null, true, "list-discord.txt; --filter-udp=50000"),
            new FakeSlot("youtube", 0, "fake:blob=b", "b", null, false, "youtube.txt; --filter-tcp=443"),
        };

        Assert.Equal(2, FakeChoice.Search(slots, "").Count);
        Assert.Single(FakeChoice.Search(slots, "DISCORD"));
        Assert.Equal("youtube", FakeChoice.Search(slots, "tcp=443 youtube.txt").Single().Section);
        Assert.Empty(FakeChoice.Search(slots, "discord tcp=443"));
    }

    [Fact]
    public void Fitting_follows_protocol_and_kind()
    {
        var all = FakeChoice.Available(null).Append(new FakeBlob("stun2.bin", FakeChoice.KindOf("stun2.bin"), false)).ToList();
        var udp = new FakeSlot("s", 0, "", "b", null, true, "");
        var tcp = udp with { Udp = false };

        Assert.Contains(FakeChoice.Fitting(udp, all), b => b.Id == "stun2.bin");
        Assert.DoesNotContain(FakeChoice.Fitting(tcp, all), b => b.Id == "stun2.bin");
        Assert.Contains(FakeChoice.Fitting(tcp, all), b => b.Id == "fake_default_tls");
    }

    [Fact]
    public void Slots_read_from_a_shipped_preset_and_rename_moves_swaps()
    {
        var file = Directory.GetFiles(ZapretPaths.PresetDirectory, "Universal V11 Lite.txt").FirstOrDefault();

        if (file is not null)
            Assert.NotEmpty(FakeChoice.Slots(new PresetReader().Load(file)));

        var s = new AppSettings { FakeSwaps = [new FakeSwap("A", "x", 0, "y.bin")] };
        Assert.Equal("B", PresetCopies.Renamed(s, "A", "B").FakeSwaps[0].Preset);
    }
}
