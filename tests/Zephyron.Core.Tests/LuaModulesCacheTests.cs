using Xunit;
using Zephyron.Zapret;

namespace Zephyron.Core.Tests;

/// <summary>Разбор модулей кэшируется, но правка файла видна сразу.</summary>
public sealed class LuaModulesCacheTests
{
    [Fact]
    public void A_changed_module_is_read_again()
    {
        var folder = Path.Combine(Path.GetTempPath(), "zy-lua-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            var file = Path.Combine(folder, "a.lua");
            File.WriteAllText(file, "function one(ctx, desync)\nend\n");

            var first = LuaModules.Scan(folder);
            Assert.Same(first, LuaModules.Scan(folder));
            Assert.Equal(["one"], first.Single().Functions);

            File.WriteAllText(file, "function one(ctx, desync)\nend\nfunction two(ctx, desync)\nend\n");

            Assert.Equal(["one", "two"], LuaModules.Scan(folder).Single().Functions);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
