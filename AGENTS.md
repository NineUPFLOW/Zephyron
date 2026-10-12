# Zephyron — заметки для агента

Десктопное приложение (WPF, .NET 8, x64-only). Основная ветка — `zephyron`
(она же default и HEAD). Ветка `main` устаревшая, трогать не нужно.

## Сборка и тесты

- Локально `dotnet` не установлен — сборка и тесты идут в GitHub Actions
  на Windows.
- Проверки в CI: `build.yml` гоняет юнит-тесты (`Zephyron.Core.Tests`,
  `Zephyron.Gui.Tests`). Сборка GUI-тестов держит `.xaml` копией в
  `Xaml/` — см. `Zephyron.Gui.Tests.csproj` (пункты `<None Include=...>`).
- `Directory.Build.props`: `<Nullable>` и `<ImplicitUsings>` включены,
  версия продукта — в `<Version>` (она же имя тега релиза).

## Разметка темы — частые падения

Тема — словари `gui/Zephyron.Gui/Theme/`: `Tokens.xaml` → `Palette.xaml`
→ `Controls.xaml` (порядок важен, см. `App.xaml` и
`tests/Zephyron.Gui.Tests/Sta.cs`).

- **`StaticResource` видит только то, что объявлено выше него.** Ссылка
  вперёд разбирается молча в тестах (шаблоны не измеряются) и падает у
  человека при показе окна: `Не удается найти ресурс с именем "..."`.
  Сторож — `ResourceKeyTests.Every_static_reference_is_declared_before_its_use`.
- **Токены, попадающие в типизированные свойства, задавай типом, а не
  числом.** `CornerRadius` — это `CornerRadius`, `BorderThickness` — это
  `Thickness`, но не `sys:Double`: число WPF переводит в строку по
  культуре системы, и на русской Windows `1.5` превращается в `1,5`,
  которое разбор не принимает. Тесты-сторожа — в
  `DesignTokensTests`/`ResourceKeyTests`.

## Выпуск релиза

- `.github/workflows/release.yml` — только `workflow_dispatch`. Тег
  создаётся автоматически как `v<Version>` из `Directory.Build.props`,
  к релизу прикладывается `Zephyron-<Version>.zip`.
- Запуск: `GH_TOKEN="$TOKEN" gh workflow run release.yml --repo NineUPFLOW/Zephyron --ref zephyron`
  (`GITHUB_TOKEN` в этой среде read-only; писать нужно через секрет `TOKEN`).
- Первый прогон после публикации может упасть на временной ошибке GitHub
  (HTTP 500 при загрузке файла), оставив пустой черновик. Черновик удалить
  и перезапустить: тег у черновика исчезает вместе с ним.
- Единый файл требует `IncludeNativeLibrariesForSelfExtract=true` (см.
  `pack.cmd`), иначе `Zephyron.exe` молча закрывается без окна.
