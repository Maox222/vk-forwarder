# vk-forwarder
Бот для пересылки сообщений из группы ВКонтакте в Telegram с возможностью отвечать прямо из Telegram.

## Возможности

📨 Из ВКонтакте в Telegram
Бот пересылает входящие сообщения, поддерживаются:

* Текстовые сообщения
* Пересылаемые и отвечаемые сообщения
* Фото
* Видео
* Документы
* Аудио
* Голосовые сообщения
* Стикеры и граффити
* Опросы
* Ссылки
* Заметки
* Товары и альбомы

📤 Из Telegram в ВКонтакте
Ответы из Telegram автоматически пересылаются обратно пользователю ВКонтакте, поддерживаются:

* Текстовые сообщения
* Фото
* Документы
* Стикеры

## Как пользоваться

1. Когда в группу ВК приходит новое сообщение — бот уведомляет вас в Telegram
2. Нажмите кнопку «Развернуть» чтобы открыть диалог
3. Пишите ответы прямо в Telegram — они автоматически пересылаются пользователю ВКонтакте
4. Нажмите «Назад» чтобы закрыть диалог

## Требования

* .NET 8 SDK — на вашем компьютере
* .NET 8 Runtime — на сервере
* Иностранный VPS Ubuntu-сервер с SSH-доступом
* Токен Telegram-бота (@BotFather)
* Токен VK Kate Mobile - https://vkhost.github.io/

## Установка .NET 8 Runtime на сервере
Подключитесь к вашему серверу по SSH (например, с помощью Putty или терминала) и выполните несколько подготовительных команд. Рекомендуется выполнять их от обычного пользователя, которого вы создали.
1. Настройка VPS
   
Обновите систему:
```bash
sudo apt update && sudo apt upgrade -y
```
2. Установите .NET Runtime:
```bash
sudo apt install -y dotnet-runtime-8.0
```

## Настройка VK

1. Откройте страницу группы → Управление → Работа с API → Ключи доступа
2. Перейдите в Long Poll API → включите и добавьте событие "Входящие сообщения"

## Деплой на сервер
1. Загрузка на сервер

>✅ В репозитории уже есть готовая сборка в папке /publish/ — собирать проект самостоятельно не нужно. Просто загрузите папку на сервер.

```bash
cd C:\Users\Имя_пользователя\Downloads
```
```bash
scp -P порт_сервера -r ./publish/ имя@айпи:/ваш_путь/папка_для_бота/
```
Если хотите собрать проект самостоятельно (например после изменений в коде):
```bash
dotnet publish -c Release -r linux-x64 --self-contained false -o ./publish
scp -P порт_сервера -r ./publish/ имя@айпи:/ваш_путь/папка_для_бота/
```
2. Настройка службы systemd
Подключитесь к серверу по SSH и создайте файл службы:
```bash
sudo nano /etc/systemd/system/vkadmin.service
```
Вставьте содержимое из файла vkadmin.service (example) и заполните переменные:
```ini
[Unit]
Description=vkadmin-bot
After=network-online.target
Wants=network-online.target

[Service]
WorkingDirectory=/ваш_путь/папка_для_бота/publish/
ExecStart=/usr/bin/dotnet /ваш_путь/папка_для_бота/publish/vk-forwarder.dll
Restart=on-failure
RestartSec=10
KillSignal=SIGINT
User=ваше_имя
Environment=TG_BOT_TOKEN=токен_бота               # @BotFather
Environment=TG_ADMIN_ID=ваш_id                    # @Getmyid_bot
Environment=VK_GROUP_TOKEN=токен_вк               # Управление → (https://vkhost.github.io/)
Environment=VK_GROUP_ID=id_группы_вк              # только цифры, без минуса

[Install]
WantedBy=multi-user.target
```
CTRL+O - сохранить, Enter - подтвердить, CTRL+X - выйти

3. Запуск службы
```bash
# Перезагрузить конфигурацию systemd
sudo systemctl daemon-reload

# Включить автозапуск при старте сервера
sudo systemctl enable vkadmin.service

# Запустить бота
sudo systemctl start vkadmin.service
```
4. Проверка
```bash
# Статус службы
sudo systemctl status vkadmin.service

# Логи в реальном времени
sudo journalctl -u vkadmin.service -f
```
### Управление ботом
```bash
# Стартовать
sudo systemctl start vkadmin.service

# Остановить
sudo systemctl stop vkadmin.service

# Перезапустить
sudo systemctl restart vkadmin.service

# Посмотреть последние 50 строк логов
sudo journalctl -u vkadmin.service -n 50 --no-pager
```
### Обновление бота
```bash
# 1. Пересобрать на компьютере
dotnet publish -c Release -r linux-x64 --self-contained false -o ./publish

# 2. Загрузить на сервер
scp -P порт_сервера -r ./publish/ имя@айпи:/ваш_путь/папка_для_бота/

# 3. Перезапустить службу на сервере
sudo systemctl restart vkadmin.service
```
