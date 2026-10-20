

[![Поддержать](https://img.shields.io/badge/%D0%BF%D0%BE%D0%B4%D0%B4%D0%B5%D1%80%D0%B6%D0%B0%D1%82%D1%8C-%E2%9D%A4-ff69b4?style=for-the-badge)](https://tips.tips/ru/000487908)

## Чем это собрано и как сверить

```
Zephyron-{VERSION}.zip   {ZIP}
Zephyron.exe   {EXE}
```

Пакет SDK .NET {SDK}, сборка Release, win-x64, self-contained, один файл.

Проверить скачанное:

```
Get-FileHash Zephyron-{VERSION}.zip -Algorithm SHA256
```

Собрать самому и сравнить:

```
git checkout v{VERSION}
pack.cmd
```

Совпасть должен `Zephyron.exe`. Архив — нет: в нём лежат winws2 и sing-box,
собранные не нами, и их версии у вас могут отличаться.
