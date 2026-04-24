using VkNet;
using VkNet.Enums.Filters;
using VkNet.Enums.StringEnums;
using VkNet.Model;

namespace vk_forwarder
{
    internal class vkService
    {

        private static long? GroupId;
        private static VkApi api;

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
                            var msg = (MessageNew)update.Instance;
                            var message = msg.Message;
                            if (message.Text != null && message.FromId != null)
                                await ProcessNewMessage(message);
                        }
                    }
                }
                catch (VkNet.Exception.LongPollKeyExpiredException)
                {
                    // Код 2: ключ истёк — обновляем только key и ts
                    Console.WriteLine("[VK LongPoll] Ключ истёк, обновляем...");
                    var fresh = api.Groups.GetLongPollServer(Convert.ToUInt64(GroupId));
                    longPollServer.Key = fresh.Key;
                    longPollServer.Ts = fresh.Ts;
                }
                catch (VkNet.Exception.LongPollInfoLostException)
                {
                    // Код 3: история событий потеряна — обновляем всё целиком
                    Console.WriteLine("[VK LongPoll] История потеряна, переподключаемся...");
                    longPollServer = api.Groups.GetLongPollServer(Convert.ToUInt64(GroupId));
                }
                catch (VkNet.Exception.LongPollOutdateException)
                {
                    // Ts устарел, - возникает при спаме сообщениями
                    Console.WriteLine("[VK LongPoll] История событий устарела или была частично утеряна");
                    await MessageDispatcher.DestroyAll();
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

                await Task.Delay(1000, ct);
            }
        }

        private static async Task ProcessNewMessage(VkNet.Model.Message message)
        {
            var userMessage = message.Text;
            var userId = (long)message.FromId;
            var user = api.Users.Get(new long[] { userId }).FirstOrDefault();


            if (!MessageDispatcher.FirstTabs.Any(tbs => tbs.User.UserId == userId))
            {
                FirstTab firstTab = new FirstTab()
                {
                    User = new VkUser(userId, user.FirstName, user.LastName)
                };
                // AddMessage triggers Messages_CollectionChanged which calls AddNewMessageToTelegram internally
                firstTab.User.AddMessage(message);
            }
            else 
            {
                FirstTab firstTab = MessageDispatcher.FirstTabs.First(tbs => tbs.User.UserId == userId);
                firstTab.User.AddMessage(message);
            }

        }

        internal static VkApi GetVkApi() => api;
        internal static long? GetGroupId() => GroupId;
    }
}
