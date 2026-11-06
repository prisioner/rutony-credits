# RutonyChat: источники и границы наблюдений

## Публичные точки входа

- [Официальный сайт RutonyChat](https://rutonychat.com/).
- [Приложение в Steam, App ID 524660](https://store.steampowered.com/app/524660/RutonyChat/).
- [Репозиторий разработчика со скриптами и Wiki](https://github.com/rutony/rutonychat-testscripts).
- [Wiki: принципы написания скриптов](https://github.com/rutony/rutonychat-testscripts/wiki/Общие-принципы-написания-скриптов-для-программы-RutonyChat).
- [Пример Script.cs в конкретном коммите](https://github.com/rutony/rutonychat-testscripts/blob/4dd99f18570eefb66e54e0fcc1a23f8094cc7925/TestScripts/Script.cs).
- [Официальная справка OBS Browser Source](https://obsproject.com/kb/browser-source).

Примеры разработчика и Wiki помогают найти точки расширения, но не
заменяют проверку фактической установленной сборки. Код стороннего
тестового проекта и бинарники RutonyChat в поставку не включены.

## Статические основания текущего устройства

Исследовалась Steam-сборка RutonyChat 24704809. Контракт runtime
содержит .NET 9, Windows Desktop и ASP.NET Core. В метаданных есть
публичные статические события ChatEventServer.OnFollower,
OnAnySubscriber, OnRaid и OnDonate; именно на них подписывается скрипт.
В исследованных связях вызовов SendAlertRaid не проходил через
ScriptsControl.NewAlert, поэтому общий NewAlert не выбран как
единственный источник рейдов. Собственный журнал нужен для периода,
сброса, восстановления и однозначного снимка титров.

Статические интерфейсы и связи вызовов подтверждают наличие механизма,
а не доставку каждого реального события. Виды подписок VK Video Live,
роль Boosty, все категории Twitch и события DonationAlerts требуют
проверки подключений на работающей программе. Их статус сохраняется
**NOT RUN** до ручного прогона. Отдельного подтверждённого общего
сигнала начала трансляции нет; новый период выбирается вручную.

[Текущее устройство](../project/architecture.md) ·
[Границы проверки](../project/quality.md)

## Уточнения ссылок и владельца канала

Для исследованной сборки SayToWindow → SystemMessage передаёт HTML в text.
Default не имеет autolink plain URL, а default-v2 имеет; собственный
экранированный anchor поддерживается проверенными stock-renderers.
Внешняя пользовательская тема и реальный клик отдельно не подтверждены.

Public GetParent/GetChatServer возвращают RutonyChat.ChatServices.ChatService;
GetChannelPath даёт Twitch login. Native OnChatMessage не содержит owner-флаг.
Для VK public OAuth.VKPlayAPI.GetChannelInfoAsync возвращает data.owner.id/nick;
поле ServiceAPI concrete-коннектора public, сам concrete-тип internal.
Это не стабильный SDK-контракт: используется guarded compatibility adapter.
Метод не запускает interactive login, но при ошибке может менять API.State.
Реальные владельцы, токены и запросы в исследовании не читались/не вызывались.

В проверенных Wiki/SDK, именах типов/public members/reference assemblies
не найден доступный скриптам общий OBS client с нужными событиями. Это
ограниченный результат поиска, не утверждение об отсутствии любых плагинов.
Текущий клиент использует [встроенный сервер OBS](https://obsproject.com/kb/remote-control-guide).
