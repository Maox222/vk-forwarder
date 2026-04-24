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
            var allText = BuildChatHistoryText(User.Messages, User.UserId);
            User.ChatHistory = allText;

            if (MessageDispatcher.SecondTabs.Count < 1 && TabId != 0)
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
            else if (TabId == 0)
            {
                // Tab not yet sent — pass to dispatcher which will queue it if SecondTab is open
                Description = $"📨{User?.FirstName} {User?.LastName}: У вас новое сообщение".Trim();
                await MessageDispatcher.AddNewMessageToTelegram(this);
            }
            else if (MessageDispatcher.SecondTabs.Count > 0 && TabId != 0)
            {
                // SecondTab is open (with a different user) and this FirstTab already exists —
                // mark as pending so FlushPendingFirstTabs will delete+resend it on back press
                Description = $"📨{User?.FirstName} {User?.LastName}: У вас новое сообщение".Trim();
                await MessageDispatcher.AddNewMessageToTelegram(this);
            }

        }

        /// <summary>
        /// Builds numbered chat history with "вы" / "ваш собеседник" labels and [вложения] markers.
        /// </summary>
        internal static string BuildChatHistoryText(
    ObservableCollection<VkNet.Model.Message> messages,
    long ownerId)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                bool isOwn = m.FromId != ownerId;
                string label = isOwn ? "\tВы\t" : "\tВаш собеседник\t";

                sb.AppendLine($"[{label}]");
                AppendMessageWithNested(sb, m, i + 1, indentLevel: 0);

                if (i < messages.Count - 1)
                    sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        // Рекурсивная функция для форматирования сообщения и всех его вложенных пересылок/ответов
        private static void AppendMessageWithNested(StringBuilder sb, VkNet.Model.Message msg, int number, int indentLevel)
        {
            string indent = new string('\t', indentLevel);

            // Основной текст сообщения
            string text = string.IsNullOrWhiteSpace(msg.Text) ? string.Empty : msg.Text;
            bool hasAttachments = msg.Attachments != null && msg.Attachments.Count > 0;
            string attachmentMark = hasAttachments ? " [Вложения]" : string.Empty;

            sb.AppendLine($"{indent}{number}. {text}{attachmentMark}");

            // Обработка ответного сообщения (ReplyMessage)
            if (msg.ReplyMessage != null)
            {
                var reply = msg.ReplyMessage;
                string replyText = string.IsNullOrWhiteSpace(reply.Text) ? string.Empty : reply.Text;
                bool replyHasAttachments = reply.Attachments != null && reply.Attachments.Count > 0;
                string replyAttachmentMark = replyHasAttachments ? " [Вложения]" : string.Empty;
                sb.AppendLine($"{indent}\t➡️ Ответ на: {replyText}{replyAttachmentMark}");

                // Если в ответном сообщении есть свои пересланные — обработаем их рекурсивно
                if (reply.ForwardedMessages != null && reply.ForwardedMessages.Count > 0)
                {
                    AppendForwardedMessages(sb, reply.ForwardedMessages, indentLevel + 1);
                }
            }

            // Обработка пересланных сообщений
            if (msg.ForwardedMessages != null && msg.ForwardedMessages.Count > 0)
            {
                AppendForwardedMessages(sb, msg.ForwardedMessages, indentLevel);
            }
        }

        // Вспомогательный метод для обработки списка пересланных сообщений
        private static void AppendForwardedMessages(StringBuilder sb, IEnumerable<VkNet.Model.Message> forwardedMessages, int indentLevel)
        {
            string indent = new string('\t', indentLevel);
            int fwdCounter = 1;
            foreach (var fwdMsg in forwardedMessages)
            {
                sb.AppendLine($"{indent}\t↩️ Пересланное");
                AppendMessageWithNested(sb, fwdMsg, fwdCounter, indentLevel + 1);
                fwdCounter++;
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

        /// <summary>
        /// Number of messages when this SecondTab was opened.
        /// Used to detect new messages that arrived while the dialog was open.
        /// </summary>
        public int MessageCountOnOpen { get; set; }

        private bool _disposed = false;

        public SecondTab(VkUser user)
        {
            User = user;
        }

        public override InlineKeyboardMarkup GetInlineKeyboard()
        {
            User.CountOfChange = 0;

            var hasAttachments = User.Messages.Any(m => m.Attachments != null && m.Attachments.Count > 0 
            || m.ForwardedMessages != null && m.ForwardedMessages.Count > 0 || m.ReplyMessage != null);

            if (hasAttachments)
            {
                return new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("Посмотреть вложения", "words:check_attachment"), 
                        InlineKeyboardButton.WithCallbackData("Назад", "words:back") }
                });
            }
            else
            {
                return new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("Назад", "words:back") }
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
            var allText = BuildChatHistoryText(User.Messages, User.UserId);
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
                MessageCountOnOpen = User.Messages.Count;
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
        public long UserId { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public int CountOfChange { get; set; }
        public string ChatHistory { get; set; }
        public ObservableCollection<VkNet.Model.Message> Messages { get; set; } = new();

        public VkUser(long id, string fName, string lName)
        {
            UserId = id;
            FirstName = fName;
            LastName = lName;
        }

        public void AddMessage(VkNet.Model.Message message)
        {
            Messages.Add(message);
            CountOfChange++;
        }
    }
}
