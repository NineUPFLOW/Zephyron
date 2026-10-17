# Память проекта Zephyron

Ведёт агент, не владелец. Здесь только то, что стоило времени; подробности по
дням — в `daily/YYYY-MM-DD.md`.

## Правила работы (жёсткие, от владельца)

- **Основную ветку не трогать никогда** без прямого согласия владельца.
  Вся работа — на ветке, которую владелец назвал (`test`/`tests`).
- **Логику не менять.** Задача редизайна — только интерфейс, анимации и то,
  что нужно, чтобы совпасть с макетом. `src/` (ядро) не трогается.
- **Источник правды по оформлению — `docs/design/mockup.html`** (владелец:
  «это и есть референс, готовый дизайн, тебе остаётся добавить туда нашу
  логику»). Других описаний дизайна не искать и не выдумывать.
- Выпуск — только по прямой просьбе «релизь X.Y.Z». «Переименуй в X.Y.Z» —
  только номер версии, не выпуск.
- **Память владелец чистит сам.** Сказано «пиши с нуля» — старые файлы
  удалены, писать заново, не восстанавливать.

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

- Сделано: плавающая таблетка навигации сверху (`TopPill`, стиль `RailItem`
  с `local:MenuIcon.Glyph`), «Главная» одной колонкой по центру
  (`ContentColumn.MaxWidth = 1080`), герой (пилюля, таймер, полоса, кнопка
  питания), метрика сети, панель режима, пары карточек, быстрые действия,
  баннер поддержки, строка внизу.
- Добавлено из макета: **журнал поверх окна** — `Views/LogConsole.xaml`
  (консоль `.log-con` + кнопка `.log-fab`), вставлен в `MainWindow` оверлеем
  (`Grid.Row=1`, `Panel.ZIndex={StaticResource ZTop}`); **виньетка**
  (`.vignette`) — `Rectangle` с радиальной кистью, `Grid.RowSpan=2`,
  `Opacity={DynamicResource BackdropDecoOpacity}`.
- Раздел `more` назван «Прочее» (иначе «Ещё» открывало бы меню с пунктом
  «Ещё»); кнопка в полосе — «Ещё».
- Слоты темы — ровно 14 (`theme.json`). Цвета макета сверх слотов
  (Ok, Purple, *Soft) — не слоты, выводятся в `Themes.Build`; в `Palette.xaml`
  объявлены теми же ключами.
- `ChevronTests.Every_collapsible_card_uses_the_shared_shape` ждёт **ровно 7**
  вхождений `{StaticResource Chevron}` — при добавлении сворачиваемой карточки
  число в тесте надо поднять.

## Грабли

- **WPF на Linux не запускается**: `Zephyron.Gui.Tests` только собираются,
  testhost падает. Разметку проверять статически (см. daily-лог): ссылки
  `{StaticResource}`/`{DynamicResource}` против ключей темы, токены против
  `docs/design-system.md`.
- **`Zephyron.Core.Tests` на Linux — 33 падения, это базовый уровень**
  (пути `C:\`, процессы, реестр, robocopy). 1551 проходит. Не регрессия.
- **dotnet ставится вручную**: `dot.net/v1/dotnet-install.sh --channel 8.0
  --install-dir /home/openhands/dotnet`; нужен `libicu-dev`. После сброса
  контейнера ставить заново.
- Привязка `{Binding Имя}` без свойства НЕ падает — молчит.
- `DropShadowEffect` — в `System.Windows.Media.Effects`.
- Один мьютекс `Local\Zephyron.Gui.interface` на сеанс Windows: при открытой
  установленной программе `build.cmd` остановит ЕЁ.
- Ресурсы XAML на Linux: `FindResource` для шрифтов/кистей в code-behind
  (`LogConsole`) работает, но проверить вживую нельзя — только глазами
  на Windows.
