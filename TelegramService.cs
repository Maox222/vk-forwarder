using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace vk_forwarder.Telegram
{
    internal class TelegramService
    {

        private static long? TelegramChatId;
        private static TelegramBotClient? _botClient;

        internal static void StartService(string tgToken, long? tgChatId)
        {
            TelegramChatId = tgChatId;
            _botClient = new TelegramBotClient(tgToken);

            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery }
            };

            _botClient.StartReceiving(
                updateHandler: HandleUpdateAsync,
                errorHandler: HandleErrorAsync,
                receiverOptions: receiverOptions
            );

        }

        internal static TelegramBotClient? GetTelegramBot() => _botClient;
        internal static long? GetTelegramId() => TelegramChatId;

        // ──────────────────────────────────────────────────────────────────
        //  Update dispatcher
        // ──────────────────────────────────────────────────────────────────

        private static async Task HandleUpdateAsync(
            ITelegramBotClient bot,
            Update update,
            CancellationToken cancellationToken)
        {
            if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery != null)
            {
                await HandleCallbackQuery(bot, update.CallbackQuery, cancellationToken);
                return;
            }

            if (update.Type == UpdateType.Message && update.Message != null)
            {
                HandleMessage(bot, update.Message, cancellationToken);
            }
        }

        // ──────────────────────────────────────────────────────────────────
        //  Callback buttons
        // ──────────────────────────────────────────────────────────────────

        private static async Task HandleCallbackQuery(
            ITelegramBotClient bot,
            CallbackQuery callbackQuery,
            CancellationToken cancellationToken)
        {
            var data = callbackQuery.Data ?? string.Empty;
            int messageId = callbackQuery.Message?.MessageId ?? 0;

            // Acknowledge the callback immediately
            try { await bot.AnswerCallbackQuery(callbackQuery.Id); } catch { }

            if (data == "words:unfold")
            {
                await MessageDispatcher.HandleUnfold(messageId);
            }
            else if (data == "words:back")
            {
                await MessageDispatcher.HandleBack(messageId);
            }
            else if (data == "words:check_attachment")
            {
                await MessageDispatcher.HandleCheckAttachments(messageId);
            }
            else if (data.StartsWith("words:show_att:"))
            {
                // data format: "words:show_att:<tabMessageId>:<messageIndex>"
                var parts = data.Split(':');
                if (parts.Length == 4
                    && int.TryParse(parts[2], out int tabMsgId)
                    && int.TryParse(parts[3], out int msgIndex))
                {
                    await MessageDispatcher.HandleShowAttachmentForIndex(tabMsgId, msgIndex);
                }
            }
            else if (data.StartsWith("words:back_attachments:"))
            {
                // data format: "words:back_attachments:<secondTabMessageId>"
                if (int.TryParse(data.Split(':')[2], out int tabMsgId))
                    await MessageDispatcher.HandleBackFromAttachments(tabMsgId);
            }
            else if (data.StartsWith("words:close_copy:"))
            {
                // data format: "words:close_copy:<messageId>"
                if (int.TryParse(data.Split(':')[2], out int msgId))
                    await MessageDispatcher.HandleCloseCopy(msgId);
            }
            else if (data == "words:delete")
            {
                await HandleDelete(messageId);
            }
        }

        // ──────────────────────────────────────────────────────────────────
        //  Incoming messages from the admin in Telegram
        // ──────────────────────────────────────────────────────────────────

        private static void HandleMessage(
            ITelegramBotClient bot,
            global::Telegram.Bot.Types.Message message,
            CancellationToken cancellationToken)
        {
            if (message.Chat.Id != TelegramChatId) return;

            // Accept messages with text, caption (photo/doc with caption), photo, document or sticker
            bool hasContent = !string.IsNullOrWhiteSpace(message.Text)
                              || !string.IsNullOrWhiteSpace(message.Caption)
                              || message.Photo != null
                              || message.Document != null
                              || message.Sticker != null;

            if (!hasContent) return;

            // Handle /copy command — intercept before forwarding to VK
            if ((message.Text ?? string.Empty).StartsWith("/copy", StringComparison.OrdinalIgnoreCase))
            {
                _ = MessageDispatcher.HandleCopyCommand(bot, message);
                return;
            }

            // If a SecondTab is open AND reply mode is active → forward to VK
            var activeSecondTab = MessageDispatcher.SecondTabs.FirstOrDefault();
            if (activeSecondTab != null)
            {
                MessageDispatcher.AddNewMessageToVk(bot, message, cancellationToken);
            }
        }

        // ──────────────────────────────────────────────────────────────────
        //  Delete dialog (Удалить button on FirstTab)
        // ──────────────────────────────────────────────────────────────────

        private static async Task HandleDelete(int messageId)
        {
            var firstTab = MessageDispatcher.FirstTabs.FirstOrDefault(t => t.TabId == messageId);
            if (firstTab == null) return;

            MessageDispatcher.FirstTabs.Remove(firstTab);
            firstTab.Dispose();

            try
            {
                await _botClient.DeleteMessage(TelegramChatId, messageId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Не удалось удалить сообщение FirstTab: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────────────────
        //  Error handler
        // ──────────────────────────────────────────────────────────────────

        private static Task HandleErrorAsync(
            ITelegramBotClient bot,
            Exception exception,
            CancellationToken cancellationToken)
        {
            Console.WriteLine($"[Telegram Error] {exception.Message}");
            return Task.CompletedTask;
        }
    }
}
