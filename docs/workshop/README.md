# Rutony Credits: Workshop-пакет

Самостоятельная сборка создаёт содержимое расширения и отдельные материалы
карточки **Credits — Титры**. Команды не запускают Steam, RutonyChat или uploader.

## Сборка из корня клона

```powershell
./scripts/build.ps1 -Check
./tests/workshop.tests.ps1
./scripts/prepare-workshop.ps1
```

`prepare-workshop.ps1` читает `dist/RutonyCredits/package-manifest.json`,
проверяет точный состав ручного пакета, длины и SHA-256. Поддерживается
schema 1 без версии; указанная version переносится в Workshop manifest.
Другой исходный пакет можно задать через `-PackageDirectory`.
Все входы сборки находятся в этом клоне, исходный пакет не изменяется.

- `dist/Workshop/RutonyCredits/` — содержимое расширения.
- `dist/WorkshopMaterials/RutonyCredits/` — этот текст, `description.txt`,
  `metadata.json`, `preview.png`, `preview.svg`, `workshop-manifest.json`.

```text
RutonyCredits/
  rutony_credits.cs
  rutony_credits.json
  LICENSE
  RutonyCredits/
    RutonyCredits.dll
    LICENSE
    web/
```

Payload не содержит установщик, PDB, RutonyChat.dll, тесты, данные
пользователя, архивы и материалы карточки. Manifest хранит revision
исходного пакета и относительные пути/хэши payload. Предыдущие результаты
остаются в `.runtime/workshop-backups/`, рабочие стадии — в `.runtime/workshop-stage/`.

`metadata.json` описывает карточку и сохраняет `status: draft` для инструмента
сборки. Это не рабочий `workshopitem.meta` uploader. Описание содержит ссылку
на публичные исходники и короткое раскрытие использования ИИ при разработке.
Обложка: PNG 512×512, менее 1 000 000 байт; SVG — её исходник.
Материалы распространяются по той же MIT-лицензии, что и расширение.

## Поиск пакета и пользовательские данные

Непустой `PackageDirectory` имеет приоритет. Иначе скрипт вызывает
`RutonyBotFunctions.GetScriptDirectory("rutony_credits.cs")` и использует
подпапку `RutonyCredits`. Если lookup пуст или завершился ошибкой,
используется ручной путь `<RutonyChat>/scripts/RutonyCredits`.
Найденный зарегистрированный item без DLL вызывает ошибку: ручная DLL
не подменяет неполный item. Для Workshop оставьте `PackageDirectory` пустым.

При наличии ручной копии и зарегистрированного Workshop-скрипта с тем же
именем автоматический поиск выбирает Workshop. Для ручной копии задайте
`PackageDirectory` явно. Используйте один активный пресет на порт,
чтобы избежать конфликта порта и двойной обработки событий.

`DataDirectory` по умолчанию — `%LOCALAPPDATA%/RutonyCredits`, независимо от
расположения пакета. Сохраняйте прежний каталог при смене способа установки;
не помещайте его внутрь Workshop, где файлами управляет Steam.
Порт по умолчанию — 18765. После замены DLL полностью перезапустите RutonyChat.

[Установка и настройка](https://github.com/prisioner/rutony-credits/blob/master/docs/manual-test.md) · [Поставка и восстановление](https://github.com/prisioner/rutony-credits/blob/master/docs/project/delivery.md) ·
[Проверки](https://github.com/prisioner/rutony-credits/blob/master/docs/project/quality.md) · [Принципы скриптов RutonyChat](https://github.com/rutony/rutonychat-testscripts/wiki/Общие-принципы-написания-скриптов-для-программы-RutonyChat)
