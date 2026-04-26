using System.Threading.Channels;
using VkNet;
using VkNet.Enums.StringEnums;
using VkNet.Model;

namespace vk_forwarder
{
    internal class vkService
    {
        private static long? GroupId;
        private static VkApi api;

        // Фоновая очередь: LongPoll цикл только пишет сюда, обработка идёт отдельно
        private static readonly Channel<VkNet.Model.Message> _messageQueue =
            Channel.CreateUnbounded<VkNet.Model.Message>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

        internal static void StartService(string vkToken, long? groupId)
        {
            GroupId = groupId;
            api = new VkApi();
            api.Authorize(new ApiAuthParams
            {
                AccessToken = vkToken
            });
        }

        internal static async Task StartListening(CancellationToken ct = default)
        {
            // Запускаем воркер обработки сообщений параллельно с LongPoll циклом
            var workerTask = Task.Run(() => ProcessMessageWorker(ct), ct);

            var longPollServer = api.Groups.GetLongPollServer(Convert.ToUInt64(GroupId));

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var history = api.Groups.GetBotsLongPollHistory(new BotsLongPollHistoryParams
                    {
                        Server = longPollServer.Server,
                        Ts = longPollServer.Ts,
                        Key = longPollServer.Key,
                        Wait = 25
                    });

                    longPollServer.Ts = history.Ts;

                    if (history.Updates == null) continue;

                    foreach (var update in history.Updates)
                    {
                        if (update.Type.Value == GroupUpdateType.MessageNew)
                        {
                            var message = (update.Instance as MessageNew)?.Message;
                            if (message?.Text != null && message.FromId != null)
                            {
                                // Просто кладём в очередь — не ждём обработки
                                _messageQueue.Writer.TryWrite(message);
                            }
                        }
                        else if (update.Type.Value == GroupUpdateType.MessageEdit)
                        {
                            var message = update.Instance as Message;
                            if (message?.Text != null && message.FromId != null)
                            {
                                _messageQueue.Writer.TryWrite(message);
                            }
                        }
                        else if (update.Type.Value == GroupUpdateType.MessageReply) 
                        {
                            var message = update.Instance as Message;
                            if (message?.Text != null && message.AdminAuthorId != null)
                            {
                                _messageQueue.Writer.TryWrite(message);
                            }
                        }
                    }
                }
                catch (VkNet.Exception.LongPollKeyExpiredException)
                {
                    Console.WriteLine("[VK LongPoll] Ключ истёк, обновляем...");
                    var fresh = api.Groups.GetLongPollServer(Convert.ToUInt64(GroupId));
                    longPollServer.Key = fresh.Key;
                    longPollServer.Ts = fresh.Ts;
                }
                catch (VkNet.Exception.LongPollInfoLostException)
                {
                    // Не вызываем DestroyAll — сообщения которые уже в очереди будут обработаны
                    Console.WriteLine("[VK LongPoll] История потеряна, переподключаемся...");
                    longPollServer = api.Groups.GetLongPollServer(Convert.ToUInt64(GroupId));
                }
                catch (VkNet.Exception.LongPollOutdateException)
                {
                    // Ts устарел при спаме — просто обновляем сервер, состояние не трогаем
                    Console.WriteLine("[VK LongPoll] Ts устарел, обновляем сервер...");
                    longPollServer = api.Groups.GetLongPollServer(Convert.ToUInt64(GroupId));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[VK LongPoll Error] {ex.Message}");
                    await Task.Delay(5000, ct);
                }

                // Убираем Task.Delay(1000) — при спаме это лишняя задержка.
                // Wait=25 в LongPoll уже даёт естественную паузу когда событий нет.
            }

            _messageQueue.Writer.Complete();
            await workerTask;
        }

        /// <summary>
        /// Фоновый воркер: читает сообщения из очереди и обрабатывает их одно за другим.
        /// Изолирует медленные операции (Users.Get, отправка в Telegram) от LongPoll цикла.
        /// </summary>
        private static async Task ProcessMessageWorker(CancellationToken ct)
        {
            await foreach (var message in _messageQueue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await ProcessNewMessage(message);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[VK Worker] Ошибка при обработке сообщения: {ex.Message}");
                }
            }
        }

        private static async Task ProcessNewMessage(VkNet.Model.Message message)
        {
            var peerId = (long)message.PeerId;

            var existingTab = MessageDispatcher.FirstTabs.FirstOrDefault(tbs => tbs.User.PeerId == peerId);

            if (existingTab == null && message.AdminAuthorId == null)
            {
                // Получаем имя пользователя только при первом сообщении — не при каждом
                var user = api.Users.Get(new long[] { peerId }).FirstOrDefault();

                FirstTab firstTab = new FirstTab()
                {
                    User = new VkUser(peerId, user?.FirstName ?? "Неизвестный", user?.LastName ?? "")
                };
                await firstTab.User.AddMessage(message);
            }
            else if (existingTab != null)
            {
                if (existingTab.User.Messages.Any(msg => msg.ConversationMessageId == message.ConversationMessageId))
                {
                    await existingTab.User.EditMessage(message);
                }
                else 
                {
                    await existingTab.User.AddMessage(message);
                }
            }
        }

        internal static VkApi GetVkApi() => api;
        internal static long? GetGroupId() => GroupId;
    }
}
