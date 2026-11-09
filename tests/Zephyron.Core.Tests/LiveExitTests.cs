using Xunit;
using Zephyron.Proxy;
using Zephyron.Supervisor;

namespace Zephyron.Core.Tests;

public sealed class LiveExitTests
{
    [Fact]
    public void Pin_note_is_taken_once_and_old_notes_are_ignored()
    {
        var path = Path.Combine(Path.GetTempPath(), "exit-pin-" + Guid.NewGuid().ToString("N") + ".txt");

        try
        {
            var stamp = ExitPin.Stamp(path);
            Assert.False(ExitPin.TryTake(path, ref stamp, out _));

            ExitPin.Write(path, "DE-1");
            Assert.True(ExitPin.TryTake(path, ref stamp, out var tag));
            Assert.Equal("DE-1", tag);
            Assert.False(ExitPin.TryTake(path, ref stamp, out _));

            ExitPin.Write(path, null);
            Assert.True(ExitPin.TryTake(path, ref stamp, out tag));
            Assert.Null(tag);

            // Записка, лежавшая до старта супервизора, не в счёт.
            var fresh = ExitPin.Stamp(path);
            Assert.False(ExitPin.TryTake(path, ref fresh, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Watch_follows_repin()
    {
        var watch = new ExitWatch("A", replacePinned: true);
        Assert.True(watch.Replaced("B"));

        watch.Repin("B");
        Assert.False(watch.Replaced("B"));

        watch.Repin(null);
        Assert.False(watch.Replaced("A"));
    }

    [Fact]
    public void Only_members_of_running_selector_can_be_selected()
    {
        Assert.True(LiveExit.CanSelect(["a", "b"], "b"));
        Assert.False(LiveExit.CanSelect(["a", "b"], "c"));
        Assert.False(LiveExit.CanSelect(null, "a"));
    }
}
