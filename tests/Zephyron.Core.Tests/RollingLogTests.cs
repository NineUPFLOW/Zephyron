using Xunit;
using Zephyron.Supervisor;

namespace Zephyron.Core.Tests;

public class RollingLogTests
{
    [Fact]
    public void A_log_cleared_from_outside_goes_on_from_the_start_without_zero_bytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"zy-log-{Guid.NewGuid():N}.log");

        try
        {
            using (var log = new RollingLog(path))
            {
                for (int i = 0; i < 100; i++)
                    log.AppendLine($"старая строка {i}");

                // Так очищает журналы окно, пока движок пишет.
                using (var other = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    other.SetLength(0);

                log.AppendLine("новая строка");
            }

            var bytes = File.ReadAllBytes(path);
            Assert.DoesNotContain((byte)0, bytes);
            Assert.Equal("новая строка" + Environment.NewLine, File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
