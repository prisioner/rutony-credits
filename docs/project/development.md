# Среда и разработка

## Стек

Windows, Git, .NET SDK 9 и Node.js для JS-проверок. SDK выбирает
`global.json` (9.0.200, `rollForward: latestFeature`), общие параметры —
`Directory.Build.props`. Внешних NuGet-пакетов нет; источники отключены
в `NuGet.Config`. Локальный сервер использует Microsoft.AspNetCore.App.
Страницы HTML/CSS/JS работают без CDN и npm-зависимостей.

Изученная среда RutonyChat: Steam build 24704809, .NET 9.
Это граница наблюдений, а не обещание совместимости со всеми сборками.
Для использования готового пакета SDK и Node.js не нужны.

## Сборка из корня

```powershell
./scripts/build.ps1 -Check
```

Команда запускает C#-проверки, JS-проверки и сборку Release, затем
создаёт `dist/RutonyCredits`. При повторной сборке этот каталог
генерируемого пакета заменяется. Персональные данные туда не помещайте.
Предыдущий пакет сохраняется в игнорируемой `.runtime/package-backups`;
новый собирается с нуля, без файлов прежнего содержимого.
Все входы сборки находятся в текущем клоне; соседние проекты не нужны.

Пакет содержит `RutonyCredits.dll`, `web/`, `rutony_credits.cs`,
`rutony_credits.json`, `install.ps1`, `manual-test.md`, `obs-automation.md`, `LICENSE`,
**Открыть титры.url** и **Открыть титры.html**. Первый ярлык использует
порт 18765, HTML позволяет указать другой. Они открывают браузер;
скрипт должен быть включён отдельно.

## Стенд без RutonyChat и аккаунтов

```powershell
$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.runtime/dotnet-cli'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
dotnet run --project tools/PreviewHost -- src/RutonyCredits/web .runtime/preview 18765
```

Панель: `http://127.0.0.1:18765/control`; экран: `/overlay` на том же
адресе. Стенд помечает данные как тестовые, использует собственный каталог
и не подключается к реальным источникам. Ctrl+C завершает именно стенд.
Не используйте порт работающего скрипта.

## Карта кода

- `src/RutonyCredits/` — модели, очередь, журнал, HTTP и страницы.
- `integration/` — автономный скрипт и JSON-схема параметров RutonyChat.
- `tests/RutonyCredits.Checks/` — консольные проверки ядра и HTTP.
- `tests/RutonyCredits.NativeChecks/` — настоящий тонкий скрипт с
  независимым fake native API, суммы/валюты, ru-RU и отключение обработчиков.
- `ObsController`/`ObsProtocol` — OBS v5; проверки используют собственный fake сервер.
- `tests/*.test.mjs` — combobox, сохранение настроек, loop/Stop и overlay.
- `tools/PreviewHost/` — безопасный ручной стенд.
- `scripts/` — сборка и установщик готового пакета.
- `launcher/` — собственные способы открытия панели.

При изменении native-скрипта отдельно компилируйте его против фактической
управляемой RutonyChat.dll: временный SDK-проект `net9.0-windows`,
`UseWPF=true`, ссылка на выбранную установленную сборку и только
`integration/rutony_credits.cs`. Машинные пути и бинарники upstream
в репозиторий не добавляются. Такая компиляция не запускает скрипт
в RutonyChat и не доказывает реальную доставку событий.

[Установка](../manual-test.md) · [Качество](quality.md) · [Совместимость runtime](../runtime-compatibility.md)

## Целостность пакета и переносимость артефактов

Canonical сборка требует Git и передаёт полный `git rev-parse HEAD` как
SourceRevisionId. `Directory.Build.props` сохраняет net9.0 и SDK из global.json,
задаёт deterministic Release, portable PDB и отображение путей текущего
клона в `/_/`. InformationalVersion сохраняет SHA. Изменённое рабочее дерево
может содержать незакоммиченные изменения: SHA описывает базовый commit,
поэтому выпуск собирайте из чистого checkout.

`-Check` также запускает `tests/delivery.tests.ps1` на временных fake
Rutony-папках в игнорируемой `.runtime`: missing/hash/traversal/duplicate
manifest, locked destination, source/target junctions, stage/backup/commit
failures, rollback failure/recovery, прерывание дочернего процесса и первая
установка. Тесты не обращаются к настоящему RutonyChat и не требуют аккаунтов.
Искусственные сбои и mock Get-Process существуют только внутри fixture.

`tools/ArtifactChecks` проверяет PE deterministic marker, portable CodeView,
соответствие PDB/PE, document paths `/_/` и SHA в InformationalVersion.
PDB остаётся build-артефактом и не входит в пользовательский пакет.
Пакет собирается отдельным `scripts/package.ps1 -SourceRevision <SHA>`
по allowlist из `scripts/package-contract.ps1`; обычная build-команда
делает это сама. Прежний dist сохраняется в `.runtime/package-backups`.

Все tracked C#-файлы имеют committed policy `*.cs text eol=lf` в
`.gitattributes`: компилятору нужны одинаковые байты исходников независимо
от `core.autocrlf`. Добавление attributes не переписывает существующий checkout.
Если в нём остались CRLF или mixed EOL, используйте свежий checkout либо явно
сохраните затронутые C#-файлы с LF, предварительно сохранив свою работу.
Gate проверяет фактические байты и отказывает до сборки checkout roots;
он не меняет исходники автоматически. BOM и текст исходников сохраняются.

После commit из чистого checkout соберите **итоговый canonical пакет** и
проверьте его тем же SDK из `global.json`:

```powershell
./scripts/build.ps1
./scripts/check-reproducibility.ps1
```

Gate требует чистый Git HEAD, LF-исходники, существующие canonical DLL и
соответствующий portable PDB, полный валидный dist manifest с текущим SHA
и DLL пакета, совпадающую с canonical DLL. Он проверяет PE/PDB identity и
revision, создаёт два настоящих detached Git worktree одного HEAD в
`.runtime/reproducibility` и собирает их тем же SDK. SHA-256 **обоих DLL и
PDB** должны совпасть между checkout roots и с canonical результатом;
DLL dist тоже должна совпасть. Старый или иной пакет/артефакт вызывает ошибку
с диагностикой; gate не пересобирает canonical пакет молча. Исправьте причину
и явно повторите `scripts/build.ps1` и gate. PDB нужен проверке, но не входит
в пользовательский пакет. Worktree и логи сохраняются для исследования.
Гарантия относится к одному чистому SHA, SDK и ОС, а не к разным SDK.

После такой сборки отдельный regression проверяет, что gate отвергает
изменённые canonical DLL и DLL пакета даже с корректным обновлённым manifest:

```powershell
pwsh -NoProfile -File tests/reproducibility.tests.ps1
```

Он временно меняет только generated артефакты, восстанавливает их точные
исходные байты в `finally` и сохраняет evidence в `.runtime`. Delivery fixtures
поддерживают PowerShell 7 и Windows PowerShell 5.1; crash-child в существующем
runner запускает `pwsh`, поэтому требует PowerShell 7 и не доказывает прерывание
процесса Windows PowerShell 5.1.


## Версия и CI

`Directory.Build.props` задаёт единственную рабочую `Version`;
InformationalVersion содержит её и полный SourceRevisionId. Текущая версия
`1.0.0` описана в [заметках выпуска](../releases/1.0.0.md).
Подробности ZIP и release manifest — в [доставке](delivery.md).
Версия не меняет runtime HTTP-контракты и native facade.

`build.ps1 -Check` также выполняет `tests/release.tests.ps1`: старые/новые
schema 1 manifest, наследование версии из props, отказы неверной version/SHA,
повреждённого/лишнего payload и ZIP, побайтовое соответствие ZIP проверенному
пакету и независимость архива от времени исходных файлов. Fixtures используют
только синтетические данные под `.runtime`. Их можно запустить отдельно:

```powershell
pwsh -NoProfile -File tests/release.tests.ps1
```

[GitHub Actions workflow](../../.github/workflows/ci.yml) запускается на push,
pull_request и workflow_dispatch, использует `windows-2025`, SDK из
`global.json` (9.0.200) и Node.js 24.16.0. SDK устанавливается в отдельную
папку runner. Отдельный PowerShell-шаг проверяет literal `sdk.version`
и передаёт его через step output в `dotnet-version` action; локальная
политика rollForward не расширяет запрос CI. Выбранные версии явно проверяются:
preinstalled SDK с более высокой feature band не должен незаметно заменить требуемую версию.
Локально сохраняется существующий `latestFeature` из global.json.

Официальные actions закреплены полным commit SHA; обновляя pin, сверьте его
с официальным release и пересмотрите изменения. Token имеет только
`contents: read`, checkout не сохраняет credentials и получает всю историю.
Нет secrets, deploy/release permissions или установки в настоящее приложение.
Новые запуски той же ветки/PR отменяют прежние; timeout job — 30 минут.
Workflow выполняет publication scanner и его fixtures, полный build-Check,
реальную reproducibility gate с двумя checkout, её негативные fixtures,
создание/проверку кандидата и offline Workshop staging с fixtures.
Артефакты хранятся 14 дней: только `dist/*.zip`, release manifest и
`dist/Workshop/**`, `dist/WorkshopMaterials/**`; bin/obj/runtime/PDB не загружаются.

Конфигурация workflow описывает состав проверок; результат конкретного запуска
смотрите в GitHub Actions для соответствующего commit. SHA в пакете при
изменённом рабочем дереве описывает только базовый commit.
