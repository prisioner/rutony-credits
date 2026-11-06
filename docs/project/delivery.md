# Поставка и восстановление

Из корня самостоятельного Git-клона выполните `./scripts/build.ps1 -Check`.
Пакет находится в `dist/RutonyCredits`: DLL, точный набор web-файлов,
`rutony_credits.cs`, JSON-схема, LICENSE, launcher и инструкции.
Включены `install.ps1`, `package-contract.ps1` и `package-manifest.json`.
SDK, RutonyChat.dll, тесты, credentials и пользовательские журналы не поставляются.
Незнакомый файл в исходной web-папке останавливает упаковку; рекурсивного
копирования произвольного содержимого нет.

Manifest schema 1 содержит module, полный Git revision, optional version и для каждого
разрешённого файла относительный путь, длину, SHA-256 и путь установки либо
null для инструкций/launcher/установщика. Manifest не включает сам себя.
Проверяются точный состав, отсутствие дубликатов, пути и все хэши.
Старые schema 1 пакеты без version остаются совместимы с установщиком;
если version указан, проверяется корректная SemVer без build metadata.
Новые пакеты получают version из `Directory.Build.props`.
Это проверка целостности комплекта, а не цифровая подпись издателя.

Полностью закройте RutonyChat. Из готового пакета запустите в Windows
PowerShell 5.1 или PowerShell 7:

```powershell
./install.ps1 -RutonyDirectory '<папка установленного RutonyChat>'
```

Установщик проверяет обычный `RutonyChat.dll`, отсутствие процесса RutonyChat,
весь package manifest, reparse points у источника/назначения и доступность
существующих заменяемых файлов. Скрипт и JSON идут в `scripts`,
DLL/web/LICENSE — в `scripts/RutonyCredits`. Чужие файлы не удаляются;
пользовательский каталог `LocalAppData/RutonyCredits` вообще не является
назначением операции. Сведения о доставках/сессиях не сбрасываются.

Перед первой заменой создаются проверенный stage и согласованная копия
**всех** принадлежащих модулю старых файлов, включая DLL, web, native CS,
JSON и LICENSE. Они находятся на том же volume:
`scripts/.RutonyCredits-install/<transaction-id>/stage` и `backup`.
`snapshot.json` хранит относительные пути, прежние хэши и факт отсутствия
нового файла до установки. Backups сохраняются и после успеха.
Повторные установщики сериализуются файловым lock.

Ошибка замены вызывает восстановление всего snapshot и ошибочный exit.
Если восстановление тоже не удалось, выводится **ROLLBACK FAILED**,
backup сохраняется, а `scripts/.RutonyCredits-install/active.json` блокирует
следующую обычную установку. После прерывания процесса marker также остаётся.
Не запускайте RutonyChat с незавершённой установкой. Уберите причину ошибки
(например, блокировку файла) и из **того же сохранённого пакета** выполните:

```powershell
./install.ps1 -RutonyDirectory '<папка установленного RutonyChat>' -Recover
```

Эта команда восстанавливает прежний комплект по проверенному snapshot и backup;
новое обновление затем запускается отдельной обычной командой. Если recovery
сообщает ошибку, сохраните marker и весь каталог transaction для ручного
восстановления; не удаляйте их ради обхода проверки.
Для ручного отката при закрытом RutonyChat восстановите все старые файлы
из одного `backup` по `snapshot.json`; у записей existed=false старого
файла не было. Инструкции ручной установки: [руководство](../manual-test.md).

Windows не предоставляет атомарную замену нескольких независимых файлов.
Прерывание может временно оставить смешанный комплект; marker делает это
видимым и требует recovery. Гарантия не распространяется на внезапную потерю
питания, повреждение диска, внешнее изменение файлов во время установки или
запуск RutonyChat после preflight. Держите приложение закрытым до успешного
завершения установки/восстановления. Новая DLL загружается после перезапуска.

Исходники распространяются по [MIT](../../LICENSE); лицензия входит в пакет
и установленный модуль. CI описан в [разработке](development.md).
Установку в RutonyChat и работу подключённых сервисов проверяйте
[вручную](../manual-test.md). [Разработка](development.md) · [Качество](quality.md)


## Версионный выпуск

Единственный источник версии — `Directory.Build.props`; рабочее значение
`1.0.0` соответствует первому выпуску MVP. Команды ниже создают локальные
артефакты, не создавая тег, GitHub Release или item Steam.
[Изменения](../../CHANGELOG.md) · [Заметки 1.0.0](../releases/1.0.0.md).

После push тега `v<Version>` и успешного завершения всех проверок кандидата
CI создаёт draft в [GitHub Releases](https://github.com/prisioner/rutony-credits/releases)
с `RutonyCredits-<Version>.zip`, `release-manifest.json` и `SHA256SUMS`. Полный SHA
исходного commit сохраняется в манифестах; ZIP bytes при переименовании
сохраняются. `Version` должна точно
совпадать с `Version` из `Directory.Build.props`; неверный тег отклоняется.
Владелец проверяет draft и публикует его вручную кнопкой **Publish**.

Существующий release для тега не перезаписывается. После частичного сбоя
перед повторным запуском проверьте состояние на GitHub; при необходимости
удалите вручную только оставшийся draft.

Из чистого Git checkout после commit выполните:

```powershell
./scripts/build.ps1 -Check
./scripts/check-reproducibility.ps1
./scripts/package-release.ps1
./scripts/package-release.ps1 -Verify
```

Результат — `dist/RutonyCredits-<version>-<full-SHA>.zip` и
`dist/release-manifest.json`. ZIP содержит ровно проверенный manual package,
включая установщик и package manifest, без внешнего каталога верхнего уровня.
Распакуйте ZIP в отдельную папку перед запуском `install.ps1`; процедура
установки и recovery выше сохраняется.

Release manifest schema 1 связывает module, version, полный revision, точные
версии SDK/Node/PowerShell, имя/размер/SHA-256 ZIP и SHA-256 package manifest.
В нём нет машинных путей или текущего времени. ZIP использует ordinal порядок
имён, фиксированные timestamps и точные проверенные байты каждого файла.
`-Verify` повторно проверяет manual package, identity и хэши обоих manifest,
весь ZIP, дубликаты, лишние/пропущенные/изменённые записи. Это целостность,
не аутентификация издателя. ZIP воспроизводим при тех же байтах пакета и
среде упаковки; разные версии PowerShell/.NET compression могут дать иной ZIP.
Различающиеся прежние ZIP/metadata сохраняются в `.runtime/release-backups`;
старые versioned ZIP другого SHA не удаляются.

CI хранит пользовательские ZIP, release manifest и отдельные Workshop
payload/materials 14 дней. Упаковщик и workflow не запускают Steam,
RutonyChat или OBS. Подписку, обновление, отписку и доставку реальных
событий проверяйте отдельно на нужной установке.
