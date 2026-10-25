using Xunit;

// Тесты идут по одному.
//
// Не осторожность, а необходимость: Application в процессе один, текущий
// каталог один, и файлы конфига собираются по относительным путям. Два теста
// разом переписали бы друг другу и то, и другое, и падение выглядело бы
// плавающим — то есть самым дорогим из возможных.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

// Тесты на утечки идут первыми. Первый же открытый в процессе ToolTip или Popup
// при подключённом клиенте UI Automation (экранный диктор, средства доступности)
// оставляет пиры, и те держат разделы навсегда — это держит Windows, а не наш код.
// Порядок классов xUnit иначе случаен, и проверки утечек краснели бы от соседей.
[assembly: TestCollectionOrderer("Zephyron.Gui.Tests.LeaksFirst", "Zephyron.Gui.Tests")]

namespace Zephyron.Gui.Tests
{
    public sealed class LeaksFirst : Xunit.ITestCollectionOrderer
    {
        public const string Name = "Утечки";

        public IEnumerable<Xunit.Abstractions.ITestCollection> OrderTestCollections(IEnumerable<Xunit.Abstractions.ITestCollection> collections) =>
            collections.OrderBy(c => c.DisplayName == Name ? 0 : 1);
    }
}
