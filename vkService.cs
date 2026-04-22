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
                catch (Exception ex)
                {
                    Console.WriteLine($"[VK LongPoll Error] {ex.Message}");
                    await Task.Delay(5000, ct); // пауза перед повтором
                }

                await Task.Delay(2000, ct);
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
                    User = new VkUser(userId, user.FirstName, user.LastName),
                    Description = $"🆕{user?.FirstName} {user?.LastName}: У вас новое сообщение".Trim()
                };
                firstTab.User.AddMessage(message);
                await MessageDispatcher.AddNewMessageToTelegram(firstTab);
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
