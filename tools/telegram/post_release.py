"""Пост о выпуске в Telegram-канал: примечания выпуска в разметке Telegram.

Зовётся из .github/workflows/telegram-release.yml. Берёт текст выпуска —
тот, что release.cmd собрал из docs/release-notes.md и подвала с хэшами, —
и переводит его в HTML, который понимает Bot API: Telegram не читает
Markdown GitHub (## заголовки, **жирный**).

Пост короткий (владелец, 26.09 и 25.10): шапка со сводкой и два списка,
«Новое» и «Исправлено», в сворачиваемых цитатах; пункт — одной фразой
с разделом впереди. Под постом кнопки «Скачать» и «Поддержать». Хвост
«Чем это собрано и как сверить» в пост не идёт вовсе.

Запуск руками, без отправки:
    RELEASE_BODY="$(cat notes.md)" RELEASE_TAG=v0.8.7 python post_release.py --dry-run

Переменные: RELEASE_BODY, RELEASE_TAG, RELEASE_URL, TELEGRAM_BOT_TOKEN,
TELEGRAM_CHAT_ID, TELEGRAM_THREAD_ID. Токен никуда не печатается — ни
в вывод, ни в ошибку.

Канал — форум, и обновления идут в отдельную тему, а не в «General»:
без TELEGRAM_THREAD_ID пост уходит в общую ленту. Номер темы — тот, что
в ссылке на неё: https://t.me/zephyron0/20774 → TELEGRAM_THREAD_ID=20774.
Пусто — пост в общую ленту, как было.
"""

import html
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request

# Предел Bot API на одно сообщение, в символах.
LIMIT = 4096

FOOTER = "## Чем это собрано"

# Кнопка под постом (владелец 15.10: «добавь кнопку поддержать в чейнджлоги
# на гитхабе и в тг»). Та же ссылка, что About.Support в программе: сменится
# донат — менять и здесь.
DONATE_URL = "https://tips.tips/ru/000487908"

# Вид изменений по заголовку «### Новое:» (или «## Новое:» до 0.14.0).
KINDS = {
    "новое": "new",
    "исправления": "fixed",
    "исправлено": "fixed",
    "удаления": "removed",
    "удалено": "removed",
}

TITLES = {
    "new": "✨ <b>Новое</b>",
    "fixed": "🛠 <b>Исправлено</b>",
    "removed": "🗑 <b>Убрано</b>",
}

# Раздел, которому в посте имя не нужно: «Общее» читается и без него.
NO_AREA = {"общее"}

# Пункт длиннее — обрезается многоточием: пост — сводка, полный текст по кнопке.
ITEM_MAX = 110


def inline(text: str) -> str:
    """Экранирует HTML и переводит **жирный** и `код` в теги Telegram."""
    text = html.escape(text, quote=False)
    text = re.sub(r"\*\*(.+?)\*\*", r"<b>\1</b>", text)
    text = re.sub(r"`([^`]+)`", r"<code>\1</code>", text)
    return text


def item_text(block: str) -> str | None:
    """
    Пункт одной целой фразой, без разметки.

    «**Свои DNS.** В разделе…» — берётся жирное начало: оно и есть суть. Но
    если фраза продолжается за жирным («**Прямые проверки** больше не уходят…»,
    «**Выключенные анимации гаснут сразу,** без перезапуска»), одно начало
    обрывалось на полуслове (владелец, 25.10) — тогда первое предложение целиком.
    Абзац без жирного начала — пояснение, в пост не идёт.
    """
    lead = re.match(r"\*\*(.+?)\*\*\s*(.*)", block, re.S)

    if not lead:
        return None

    bold, rest = lead.group(1).strip(), lead.group(2).strip()

    if bold.endswith(",") or (rest and rest[0].islower()):
        plain = re.sub(r"\*\*|`", "", block).replace("\n", " ")
        text = re.split(r"(?<=[.!?])\s", plain, maxsplit=1)[0]
    else:
        text = bold

    text = text.strip().rstrip(".,:;").strip()

    if len(text) > ITEM_MAX:
        text = text[: ITEM_MAX - 1].rstrip() + "…"

    return text


def lowered(text: str) -> str:
    """После имени раздела — со строчной: «VPN: компактнее». «Hysteria2», «IP» — как есть."""
    if len(text) > 1 and ("А" <= text[0] <= "Я" or text[0] == "Ё") and text[1].islower():
        return text[0].lower() + text[1:]
    return text


def heading(text: str) -> str:
    """Заголовок без нумерации и двоеточия: «1.1 Новое:» — «Новое»."""
    return re.sub(r"^\d+(\.\d+)*\.?\s+", "", text.strip()).rstrip(":").strip()


def convert(body: str) -> dict[str, list[str]]:
    """
    Примечания выпуска сводкой: пункты по виду изменений, раздел — в начале пункта.

    С 0.14.0 разделы — части программы («## Десинк»), а вид — «### Новое:».
    В посте наоборот: списки «Новое» и «Исправлено», а раздел — в пункте
    («VPN: компактнее»). Раньше пост повторял «Новое/Исправления» под каждым
    разделом и выходил лентой на экран (владелец, 25.10). Чейнджлоги до 0.14.0,
    где «## Новое:» — сам раздел, читаются так же, без раздела в пункте.
    """
    notes = body.split(FOOTER, 1)[0]
    lists: dict[str, list[str]] = {"new": [], "fixed": [], "removed": []}
    area: str | None = None
    kind = "new"

    for raw in re.split(r"\n\s*\n", notes.replace("\r\n", "\n")):
        block = raw.strip()

        if not block or block.startswith("# "):
            continue

        if block.startswith("#"):
            title = heading(block.lstrip("#"))
            known = KINDS.get(title.lower())

            if known:
                kind = known
                if block.startswith("## "):
                    area = None
            elif block.startswith("## "):
                area = None if title.lower() in NO_AREA else title
                kind = "new"
            continue

        text = item_text(block)

        if text:
            lists[kind].append(inline(f"{area}: {lowered(text)}" if area else text))

    return lists


def compose(body: str, tag: str, url: str) -> str:
    """
    Пост: шапка со сводкой и списки в сворачиваемых цитатах.

    Сворачиваемая цитата (Bot API 7.4) в ленте показывает пару строк, весь
    список — по нажатию: пост короткий, и ничего не теряется. Ссылка на выпуск —
    кнопкой под постом (send), а не строкой в тексте.
    """
    version = tag.lstrip("v")
    lists = convert(body)

    counts = []
    if lists["new"]:
        counts.append(f"новое: {len(lists['new'])}")
    if lists["fixed"]:
        counts.append(f"исправлено: {len(lists['fixed'])}")

    head = f"🚀 <b>Zephyron {html.escape(version)}</b>"
    if counts:
        head += "\n" + " · ".join(counts)

    def build(limit: int | None) -> str:
        parts = [head]

        for kind in ("new", "fixed", "removed"):
            items = lists[kind]

            if not items:
                continue

            shown = items if limit is None else items[:limit]
            lines = "\n".join("• " + item for item in shown)

            if len(shown) < len(items):
                lines += f"\n…и ещё {len(items) - len(shown)} — по кнопке «Скачать»"

            parts.append(f"{TITLES[kind]}\n<blockquote expandable>{lines}</blockquote>")

        return "\n\n".join(parts)

    # Не влезает — меньше пунктов в каждом списке, а не обрыв посреди тега:
    # разорванный <b> Telegram отвергнет всё сообщение целиком.
    text = build(None)
    limit = max((len(items) for items in lists.values()), default=0)

    while len(text) > LIMIT and limit > 1:
        limit -= 1
        text = build(limit)

    return text


def send(text: str, url: str) -> None:
    token = os.environ["TELEGRAM_BOT_TOKEN"]
    chat = os.environ["TELEGRAM_CHAT_ID"]
    thread = os.environ.get("TELEGRAM_THREAD_ID", "").strip()

    fields = {
        "chat_id": chat,
        "text": text,
        "parse_mode": "HTML",
        "disable_web_page_preview": "true",
        # Кнопки под сообщением, а не ссылки в тексте: так их видно сразу,
        # и пост не удлиняется.
        "reply_markup": json.dumps({"inline_keyboard": [[
            {"text": "📥 Скачать", "url": url},
            {"text": "💛 Поддержать", "url": DONATE_URL},
        ]]}),
    }

    # Канал — форум: без номера темы пост уходит в общую ленту, а не
    # в тему обновлений.
    if thread:
        fields["message_thread_id"] = thread

    data = urllib.parse.urlencode(fields).encode()

    request = urllib.request.Request(f"https://api.telegram.org/bot{token}/sendMessage", data=data)

    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            answer = json.load(response)
    except urllib.error.HTTPError as error:
        # Описание ошибки от Telegram — без адреса запроса: в нём токен.
        try:
            answer = json.load(error)
        except ValueError:
            answer = {"description": f"HTTP {error.code}"}

    if not answer.get("ok"):
        sys.exit(f"Telegram отказал: {answer.get('description', 'без объяснения')}")

    if thread:
        print("Отправлено в", chat, "тему", thread)
    else:
        print("Отправлено в", chat)


def main() -> None:
    body = os.environ.get("RELEASE_BODY", "")
    tag = os.environ.get("RELEASE_TAG", "")
    url = os.environ.get("RELEASE_URL") or f"https://github.com/NineUPFLOW/Zephyron/releases/tag/{tag}"

    if not body.strip() or not tag:
        sys.exit("Нет текста выпуска или тега — отправлять нечего.")

    text = compose(body, tag, url)

    if "--dry-run" in sys.argv:
        print(text)
        print(f"\n[кнопки под постом: 📥 Скачать → {url} · 💛 Поддержать → {DONATE_URL}]")
        print(f"[{len(text)} символов из {LIMIT}]")
        return

    send(text, url)


if __name__ == "__main__":
    main()
