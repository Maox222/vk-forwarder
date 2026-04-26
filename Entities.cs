using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using vk_forwarder.Telegram;
using VkNet.Model;

namespace vk_forwarder
{
    public class FirstTab : IDisposable
    {
        public int TabId { get; set; }
        private bool _disposed = false;

        private VkUser _user;

        public VkUser User
        {
            get { return _user; }
            set
            {
                _user = value;
                _user.Messages.CollectionChanged += Messages_CollectionChanged;
            }
        }

        public string Description { get; set; }


        public virtual InlineKeyboardMarkup GetInlineKeyboard()
        {
            return new InlineKeyboardMarkup(new[]
                {
                    new[] {
                        InlineKeyboardButton.WithCallbackData("Развернуть", "words:unfold"),
                        InlineKeyboardButton.WithCallbackData("Удалить", "words:delete")
                    }
                });
        }

        internal async virtual void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_disposed) return;
            var allText = BuildChatHistoryText(User.Messages, User.PeerId, User.FirstName, User.LastName);
            User.ChatHistory = allText;

            if (MessageDispatcher.SecondTabs.Count < 1 && TabId != 0 && User.Messages[e.NewStartingIndex].AdminAuthorId == null)
            {
                // SecondTab closed, tab already sent — delete old and resend to trigger a push notification
                try
                {
                    var bot = TelegramService.GetTelegramBot();
                    var chatId = TelegramService.GetTelegramId();

                    Description = $"📨{User?.FirstName} {User?.LastName}: У вас новое сообщение".Trim();

                    await bot.DeleteMessage(chatId, TabId);

                    var sent = await bot.SendMessage(
                        chatId,
                        text: Description,
                        replyMarkup: GetInlineKeyboard()
                    );
                    TabId = sent.MessageId;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Не удалось обновить сообщение в 1 блоке: {ex.Message}");

                }
            }
            else if (TabId == 0 && User.Messages[e.NewStartingIndex].AdminAuthorId == null)
            {
                // Tab not yet sent — pass to dispatcher which will queue it if SecondTab is open
                Description = $"📨{User?.FirstName} {User?.LastName}: У вас новое сообщение".Trim();
                await MessageDispatcher.AddNewMessageToTelegram(this);
            }
            else if (MessageDispatcher.SecondTabs.Count > 0 && TabId != 0 && User.PeerId != MessageDispatcher.SecondTabs.FirstOrDefault().User?.PeerId
                && User.Messages[e.NewStartingIndex].AdminAuthorId == null)
            {
                // SecondTab is open (with a different user) and this FirstTab already exists —
                // mark as pending so FlushPendingFirstTabs will delete+resend it on back press
                Description = $"📨{User?.FirstName} {User?.LastName}: У вас новое сообщение".Trim();
                await MessageDispatcher.AddNewMessageToTelegram(this);
            }
            else if (TabId != 0 && User.Messages[e.NewStartingIndex].AdminAuthorId != null)
            {
                // If messages sent by admin outside of Telegram
                try
                {
                    var bot = TelegramService.GetTelegramBot();
                    var chatId = TelegramService.GetTelegramId();

                    string newDescription = $"✔️{User.FirstName} {User.LastName}: Прочитано";
                    if (Description != newDescription) 
                    {
                        Description = newDescription;
                        await bot.EditMessageText(TelegramService.GetTelegramId(), TabId, Description, replyMarkup: GetInlineKeyboard());
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка при редактировании FirstTab на Прочитано: {ex.Message}");
                }
            }

        }

        /// <summary>
        /// Builds numbered chat history with "вы" / "ваш собеседник" labels and [вложения] markers.
        /// Forwarded messages are numbered locally within each top-level message (f1, f2, f3…).
        /// </summary>
        internal static string BuildChatHistoryText(ObservableCollection<VkNet.Model.Message> messages, long ownerId, string firstName, string lastName)
        {
            var sb = new StringBuilder();

            for (int i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                bool isOwn = m.FromId != ownerId;
                string label = isOwn ? "\tВы\t" : $"\t{firstName} {lastName}\t";

                sb.AppendLine($"[{label}]");

                // Локальные счётчики — сбрасываются для каждого сообщения
                int localFwd = 1;
                int localReply = 1;
                AppendMessageWithNested(sb, m, i + 1, indentLevel: 0, ref localFwd, ref localReply);

                if (i < messages.Count - 1)
                    sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        private static void AppendMessageWithNested(StringBuilder sb, VkNet.Model.Message msg, int number, int indentLevel, ref int fwdCounter, ref int replyCounter)
        {
            string indent = new string('\t', indentLevel);

            string text = string.IsNullOrWhiteSpace(msg.Text) ? string.Empty : msg.Text;
            bool hasAttachments = msg.Attachments != null && msg.Attachments.Count > 0;
            string attachmentMark = hasAttachments ? " [Вложения]" : string.Empty;

            sb.AppendLine($"{indent}{number}. {text}{attachmentMark}");

            if (msg.ReplyMessage != null)
            {
                int currentReply = replyCounter;
                replyCounter++;

                var reply = msg.ReplyMessage;
                string replyText = string.IsNullOrWhiteSpace(reply.Text) ? string.Empty : reply.Text;
                bool replyHasAttachments = reply.Attachments != null && reply.Attachments.Count > 0;
                string replyAttachmentMark = replyHasAttachments ? " [Вложения]" : string.Empty;
                sb.AppendLine($"{indent}\t➡️ Ответ на: {replyText}{replyAttachmentMark}");

                if (reply.ForwardedMessages != null && reply.ForwardedMessages.Count > 0)
                    AppendForwardedMessages(sb, reply.ForwardedMessages, indentLevel + 1, ref fwdCounter, ref replyCounter);
            }

            if (msg.ForwardedMessages != null && msg.ForwardedMessages.Count > 0)
                AppendForwardedMessages(sb, msg.ForwardedMessages, indentLevel, ref fwdCounter, ref replyCounter);
        }

        private static void AppendForwardedMessages(StringBuilder sb, IEnumerable<VkNet.Model.Message> forwardedMessages, int indentLevel, ref int fwdCounter, ref int replyCounter)
        {
            string indent = new string('\t', indentLevel);
            foreach (var fwdMsg in forwardedMessages)
            {
                int currentIndex = fwdCounter;
                fwdCounter++;

                sb.AppendLine($"{indent}\t↩️ Пересланное");
                AppendMessageWithNested(sb, fwdMsg, currentIndex, indentLevel + 1, ref fwdCounter, ref replyCounter);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            User.Messages.CollectionChanged -= Messages_CollectionChanged;

            _disposed = true;
        }
    }

    public class SecondTab : FirstTab, IDisposable
    {
        public int AdditionalMessageId { get; set; }

        /// <summary>
        /// Tracks all message IDs sent by the bot inside this SecondTab session
        /// so they can be deleted later.
        /// </summary>
        public List<int> BotMessageIds { get; set; } = new List<int>();

        /// <summary>
        /// Whether the bot is waiting for the user to enter an attachment message number.
        /// </summary>
        public bool IsAwaitingAttachmentIndex { get; set; } = false;

        private bool _disposed = false;

        public SecondTab(VkUser user)
        {
            User = user;
        }

        public override InlineKeyboardMarkup GetInlineKeyboard()
        {

            var hasAttachments = User.Messages.Any(m => m.Attachments != null && m.Attachments.Count > 0 
            || m.ForwardedMessages != null && m.ForwardedMessages.Count > 0 || m.ReplyMessage?.Attachments != null && m.ReplyMessage.Attachments.Count > 0);

            if (hasAttachments)
            {
                return new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("📎 Вложения", "words:check_attachment"), 
                        InlineKeyboardButton.WithCallbackData("🔙 Назад", "words:back") }
                });
            }
            else
            {
                return new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад", "words:back") }
                });
            }
        }

        internal async override void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_disposed) return;

            if (User?.Messages.Count < 1)
            {
                User.ChatHistory = string.Empty;
                return;
            }

            // Update chat history text
            var allText = BuildChatHistoryText(User.Messages, User.PeerId, User.FirstName, User.LastName);
            User.ChatHistory = allText;

            try
            {
                var bot = TelegramService.GetTelegramBot();
                if (bot == null || _disposed) return;

                await bot.EditMessageText(
                    chatId: TelegramService.GetTelegramId(),
                    messageId: TabId,
                    text: User.ChatHistory,
                    replyMarkup: GetInlineKeyboard(),
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true }
                );

                // Сообщения отображены в открытом диалоге — считаем прочитанными.
                // HandleBack проверяет MessageCountOnOpen, чтобы решить "новые или нет":
                // раз мы уже показали все сообщения, обновляем счётчик.
                //MessageCountOnOpen = User.Messages.Count;
            }
            catch (Exception ex)
            {
                if (!_disposed)
                    Console.WriteLine($"Не удалось обновить сообщение: {ex.Message}");
            }
        }

        /// <summary>
        /// Deletes all bot messages tracked in this SecondTab session.
        /// </summary>
        public async Task DeleteAllBotMessages()
        {
            var bot = TelegramService.GetTelegramBot();
            var chatId = TelegramService.GetTelegramId();

            foreach (var msgId in BotMessageIds.ToList())
            {
                try
                {
                    await bot.DeleteMessage(chatId, msgId);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Не удалось удалить сообщение {msgId}: {ex.Message}");
                }
            }
            BotMessageIds.Clear();
        }

        public void Dispose() 
        {
            if (_disposed) return;

            User.Messages.CollectionChanged -= Messages_CollectionChanged;

            _disposed = true;
        }
    }

    public class VkUser
    {
        public long PeerId { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string ChatHistory { get; set; }
        public ObservableCollection<VkNet.Model.Message> Messages { get; set; } = new();

        public VkUser(long id, string fName, string lName)
        {
            PeerId = id;
            FirstName = fName;
            LastName = lName;
        }

        public async Task AddMessage(VkNet.Model.Message message)
        {
            Messages.Add(message);

            // If dialog is open Mark As Read Instantly
            if (MessageDispatcher.SecondTabs.Count > 0 && MessageDispatcher.SecondTabs.Any(tbs => tbs.User.PeerId == PeerId)) 
            {
                await vkService.GetVkApi().Messages.MarkAsReadAsync(PeerId.ToString());
            }
        }
        public async Task EditMessage(VkNet.Model.Message message)
        {
            var foundMessage = Messages.FirstOrDefault(msg => msg.ConversationMessageId == message.ConversationMessageId);

            if (foundMessage != null)
            {
                foundMessage.Text = message.Text + " (ред.)" ?? string.Empty;
                if (message.Attachments.Count > 0) foundMessage.Attachments = message.Attachments;

                int index = Messages.IndexOf(foundMessage);
                if (index != -1)
                {
                    Messages[index] = foundMessage;

                    // If dialog is open Mark As Read Instantly
                    if (MessageDispatcher.SecondTabs.Count > 0 && MessageDispatcher.SecondTabs.Any(tbs => tbs.User.PeerId == PeerId))
                    {
                        await vkService.GetVkApi().Messages.MarkAsReadAsync(PeerId.ToString());
                    }
                }
            }

        }
    }
}
