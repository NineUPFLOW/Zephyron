# Память проекта Zephyron

Ведёт агент, не владелец. Только то, что стоило времени; подробности по дням —
в `daily/YYYY-MM-DD.md`.

## Правила работы (жёсткие, от владельца)

- **Основную ветку (`zephyron`) не трогать никогда** без прямого согласия.
  Вся работа — на ветке, которую владелец назвал (сейчас `test`).
- **Логику не менять.** Редизайн — только интерфейс, анимации и то, что нужно,
  чтобы совпасть с макетом. `src/` (ядро) не трогается.
- **Источник правды по оформлению — `docs/design/mockup.html`** (владелец:
  «это и есть референс, готовый дизайн»). Других описаний дизайна не искать.
- Выпуск — только по прямой просьбе «релизь X.Y.Z». «Переименуй в X.Y.Z» —
  только номер версии, не выпуск.
- Память владелец чистит сам. Сказано «пиши с нуля» — писать заново.

## Проект

- Zephyron — WPF-обход блокировок для Windows: окно `gui/Zephyron.Gui`
  + ядро `src/` (консоль удалена 23.09; остался `zy` — инструмент разбора).
- Родственный репозиторий `RixyPow/netzapret` — параллельная копия (не форк):
  NetZapret→Zephyron, `nz.cmd`→`zy.cmd`, `CLAUDE.md`→`AGENTS.md`,
  RixyPow→NineUPFLOW. Код совпадает ~99%, их правки переносимы.
- Версия — `<Version>` в `Directory.Build.props`, не в cmd.
- Релиз — ручной: `.github/workflows/release.yml`, `workflow_dispatch`,
  ref `zephyron`. `build.yml` — push в main/zephyron + PR, только тесты.
- Донат-ссылка — `About.Support` (`src/Zephyron.Core/About.cs`);
  `.github/FUNDING.yml` раскомментирован с 14.10.

## Оформление (ветка test)

- **Шрифты — Inter и Fira Code, встроены в сборку** (`gui/Zephyron.Gui/Assets/
  Fonts/`, `<Resource>` в `Zephyron.Gui.csproj`). Имена семейств — «Inter»
  и «Fira Code». `Themes.Bundled()` превращает имя в pack-адрес
  (`pack://application:,,,/Zephyron;component/…` — сборка названа явно, иначе
  из тестов не находится). `Font()` без файлов темы и `BuiltInFonts()` для
  `Palette.xaml`/`Light.xaml` зовут `Bundled` — иначе окно без темы шло бы
  системным Segoe UI. `theme.json` dark/light тоже Inter/Fira Code.
- **Узор фона** (сетка, свечения, волны `Aurora.cs`, точки `Particles.cs`,
  виньетка) живёт в `MainWindow.xaml` под содержимым, виден при
  `BackdropDecoOpacity = 1` (встроенная тема без картинки).
- **`Chrome` прозрачен у встроенных тем** (Themes.Build) — разделы не заслоняют
  узор. **`HomeBackdrop`** — подложка «Главной»: прозрачна у встроенных тем,
  цвет подложки у темы с картинкой (её арт — в правой колонке).
- «Главная» — одной колонкой по центру (`ContentColumn.MaxWidth = 1080`,
  правая звезда отдана арту темы `ArtColumn`/`ArtLayer`).
- Пилюля героя красится по состоянию (`ShowDot` → `PillSoft`/`PillLine`):
  заливка *Soft, рамка *Line, подпись цветом. «Работает» — `Ok` (зелёный),
  не `Accent`.
- Кнопка журнала (`LogConsole`) прячется, пока виден мастер первого запуска
  (свои кнопки в том же углу).

## Грабли

- **WPF на Linux не запускается**: `Zephyron.Gui.Tests` только собираются,
  testhost падает («You must install or update .NET», `Microsoft.WindowsDesktop.App`).
  Разметку проверять статически: ссылки `{StaticResource}`/`{DynamicResource}`
  против ключей темы, токены против `docs/design-system.md`.
- **`Zephyron.Core.Tests` на Linux — 33 падения, это базовый уровень**
  (пути `C:\`, процессы, реестр, robocopy, планировщик). 1551 проходит.
  Не регрессия.
- **dotnet ставится вручную и исчезает между сессиями**:
  `curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0
  --install-dir $HOME/dotnet`; **обязательно** `sudo apt-get install -y libicu-dev`
  — без libicu CLI падает на старте (`CultureData.get_Invariant`).
- **В `.csproj` комментарий XML не может содержать `--`** (MSB4025). Не писать
  длинное тире из двух дефисов.
- Слоты темы — **ровно 14** (`theme.json`). Цвета макета сверх слотов
  (Ok, Purple, *Soft, *Line) выводятся в `Themes.Build`; в `Palette.xaml`
  объявлены теми же ключами (тест `ABuiltThemeHasEveryKeyOfTheXamlPalette`).
  Новый ключ в `Palette.xaml` обязан появиться и в `Build` для каждой ветки.
- `ChevronTests` считает вхождения `{StaticResource Chevron}` — при добавлении
  сворачиваемой карточки число в тесте надо поднять.
- Терминал: не давать две команды в одном вызове без `&&`/`;`; многострочный
  heredoc не принимается — писать файл через `file_editor`.
