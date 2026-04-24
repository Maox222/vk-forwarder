using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using vk_forwarder.Telegram;
using VkNet.Enums.StringEnums;
using VkNet.Model;

namespace vk_forwarder
{
    internal class MessageDispatcher
    {
        internal static HashSet<FirstTab> FirstTabs = new HashSet<FirstTab>();
        private static TelegramBotClient botClient = TelegramService.GetTelegramBot();
        internal static HashSet<SecondTab> SecondTabs = new HashSet<SecondTab>();

        /// <summary>
        /// Queue of FirstTabs waiting to be sent while a SecondTab is open.
        /// </summary>
        private static Queue<FirstTab> _pendingFirstTabs = new Queue<FirstTab>();

        // ──────────────────────────────────────────────────────────────────
        //  Adding new dialogs / messages
        // ──────────────────────────────────────────────────────────────────

        internal static async Task AddNewMessageToTelegram(FirstTab firstTab)
        {
            // If a SecondTab is open, queue this dialog for later
            if (SecondTabs.Count > 0)
            {
                _pendingFirstTabs.Enqueue(firstTab);
                return;
            }

            await SendFirstTabNow(firstTab);
        }

        private static async Task SendFirstTabNow(FirstTab firstTab)
        {
            string telegramMessageText = firstTab.Description;
            var inlineKeyboard = firstTab.GetInlineKeyboard();

            try
            {
                var sentMessage = await botClient.SendMessage(
                    TelegramService.GetTelegramId(),
                    text: telegramMessageText,
                    replyMarkup: inlineKeyboard
                );

                firstTab.TabId = sentMessage.MessageId;
                FirstTabs.Add(firstTab);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при отправке в Telegram: {ex.Message}");
            }
        }

        /// <summary>
        /// Called when a SecondTab is closed (back button pressed) to flush the queue.
        /// Skips tabs whose user already has an active FirstTab — just updates the description instead.
        /// </summary>
        private static async Task FlushPendingFirstTabs()
        {
            // Send queued tabs for users who wrote for the first time while SecondTab was open
            var deduplicated = _pendingFirstTabs
                .GroupBy(t => t.User.UserId)
                .Select(g =>
                {
                    var toDispose = g.SkipLast(1).ToList();
                    foreach (var old in toDispose) old.Dispose();
                    return g.Last();
                })
                .ToList();
            _pendingFirstTabs.Clear();

            foreach (var tab in deduplicated)
            {
                // If a FirstTab for this user already exists — delete old and resend for notification
                var existing = FirstTabs.FirstOrDefault(t => t.User.UserId == tab.User.UserId);
                if (existing != null)
                {
                    try
                    {
                        existing.Description = tab.Description;
                        var chatId = TelegramService.GetTelegramId();

                        await botClient.DeleteMessage(chatId, existing.TabId);

                        var sent = await botClient.SendMessage(
                            chatId,
                            text: existing.Description,
                            replyMarkup: existing.GetInlineKeyboard()
                        );
                        existing.TabId = sent.MessageId;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[FlushPending] Не удалось обновить существующий FirstTab: {ex.Message}");
                    }

                    // Dispose only if it's a different object (a newly created tab for an already-existing user).
                    // If tab IS existing (same reference — queued from FirstTab.Messages_CollectionChanged),
                    // don't dispose it or we'll kill the live tab.
                    if (!ReferenceEquals(tab, existing))
                        tab.Dispose();
                    continue;
                }

                await SendFirstTabNow(tab);
            }
        }

        internal static async void AddNewMessageToVk(
            ITelegramBotClient botClient,
            global::Telegram.Bot.Types.Message message,
            CancellationToken cancellationToken)
        {
            if (SecondTabs == null || SecondTabs.Count == 0) return;

            SecondTab secondTab = SecondTabs.First();


            // Track this user message so it can be deleted afterwards
            secondTab.BotMessageIds.Add(message.MessageId);

            // ── Forward attachments from Telegram → VK ──────────────────
            var vkAttachments = new List<VkNet.Model.MediaAttachment>();

            try
            {
                vkAttachments = await UploadTelegramAttachmentsToVk(botClient, message, secondTab.User.UserId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VK] Ошибка при загрузке вложений: {ex.Message}");
            }

            // Add the outgoing message to local history
            secondTab.User.AddMessage(new VkNet.Model.Message()
            {
                Text = message.Text ?? message.Caption ?? (vkAttachments.Count > 0 ? "[Ваши вложения]" : string.Empty),
                FromId = message.From?.Id ?? 0
            });

            try
            {
                var sendParams = new VkNet.Model.MessagesSendParams()
                {
                    UserId = secondTab.User.UserId,
                    RandomId = new Random().Next(),
                    Message = message.Text ?? message.Caption ?? string.Empty
                };

                if (vkAttachments.Count > 0)
                    sendParams.Attachments = vkAttachments;

                vkService.GetVkApi().Messages.Send(sendParams);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при отправке в VK: {ex.Message}");
            }

            // Delete the user's reply message from Telegram after sending
            try
            {
                await botClient.DeleteMessage(TelegramService.GetTelegramId(), message.MessageId);
                secondTab.BotMessageIds.Remove(message.MessageId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Telegram] Не удалось удалить сообщение пользователя: {ex.Message}");
            }
        }

        /// <summary>
        /// Downloads files attached to a Telegram message, uploads them to VK,
        /// and returns typed MediaAttachment objects ready for MessagesSendParams.
        /// </summary>
        private static async Task<List<VkNet.Model.MediaAttachment>> UploadTelegramAttachmentsToVk(
            ITelegramBotClient bot,
            global::Telegram.Bot.Types.Message message,
            long vkPeerId)
        {
            var result = new List<VkNet.Model.MediaAttachment>();
            var vkApi = vkService.GetVkApi();

            // ── Photo ────────────────────────────────────────────────────
            if (message.Photo != null && message.Photo.Length > 0)
            {
                var largest = message.Photo.OrderByDescending(p => p.Width).First();
                var bytes = await DownloadTelegramFile(bot, largest.FileId);
                if (bytes != null)
                {
                    var uploadServer = vkApi.Photo.GetMessagesUploadServer(vkService.GetGroupId());
                    var uploadResponse = await UploadFileToVkServer(
                        uploadServer.UploadUrl, bytes, "photo.jpg", "image/jpeg", "photo");
                    var saved = vkApi.Photo.SaveMessagesPhoto(uploadResponse);
                    if (saved != null && saved.Count > 0)
                        result.Add(saved[0]);
                }
            }

            // ── Document / file ──────────────────────────────────────────
            if (message.Document != null)
            {
                var bytes = await DownloadTelegramFile(bot, message.Document.FileId);
                if (bytes != null)
                {
                    var uploadServer = vkApi.Docs.GetMessagesUploadServer(vkPeerId);
                    var uploadResponse = await UploadFileToVkServer(
                        uploadServer.UploadUrl, bytes,
                        message.Document.FileName ?? "file.bin",
                        message.Document.MimeType ?? "application/octet-stream",
                        "file");
                    var saved = vkApi.Docs.Save(uploadResponse, message.Document.FileName ?? "file", null);
                    if (saved?.Count > 0)
                        result.Add(saved[0].Instance);
                }
            }

            // ── Sticker (treat as document) ──────────────────────────────
            if (message.Sticker != null)
            {
                var bytes = await DownloadTelegramFile(bot, message.Sticker.FileId);
                if (bytes != null)
                {
                    var uploadServer = vkApi.Docs.GetMessagesUploadServer(vkPeerId);
                    var uploadResponse = await UploadFileToVkServer(
                        uploadServer.UploadUrl, bytes, "sticker.webp", "image/webp", "file");
                    var saved = vkApi.Docs.Save(uploadResponse, "sticker", null);
                    if (saved?.Count > 0)
                        result.Add(saved[0].Instance);
                }
            }

            return result;
        }

        /// <summary>Downloads raw bytes of a Telegram file by fileId.</summary>
        private static async Task<byte[]?> DownloadTelegramFile(ITelegramBotClient bot, string fileId)
        {
            try
            {
                var file = await bot.GetFile(fileId);
                using var ms = new MemoryStream();
                await bot.DownloadFile(file.FilePath!, ms);
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TG Download] Ошибка: {ex.Message}");
                return null;
            }
        }

        /// <summary>POSTs file bytes as multipart/form-data to a VK upload URL.</summary>
        private static async Task<string> UploadFileToVkServer(
            string uploadUrl, byte[] bytes, string fileName, string mimeType, string fieldName)
        {
            using var httpClient = new HttpClient();
            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(bytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);
            content.Add(fileContent, fieldName, fileName);

            var response = await httpClient.PostAsync(uploadUrl, content);
            return await response.Content.ReadAsStringAsync();
        }

        // ──────────────────────────────────────────────────────────────────
        //  Callback button handlers
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Handles "Развернуть" — opens a SecondTab for the tapped FirstTab.
        /// </summary>
        internal static async Task HandleUnfold(int messageId)
        {
            var firstTab = FirstTabs.FirstOrDefault(t => t.TabId == messageId);
            if (firstTab == null) return;

            try
            {
                var secondTab = new SecondTab(firstTab.User)
                {
                    MessageCountOnOpen = firstTab.User.Messages.Count
                };

                var sentMessage = await botClient.SendMessage(
                    chatId: TelegramService.GetTelegramId(),
                    text: firstTab.User.ChatHistory,
                    replyMarkup: secondTab.GetInlineKeyboard(),
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true }
                );

                secondTab.TabId = sentMessage.MessageId;
                SecondTabs.Add(secondTab);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при открытии SecondTab: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles "Назад" — closes SecondTab, deletes all tracked bot messages,
        /// restores FirstTab view, flushes pending dialogs.
        /// </summary>
        internal static async Task HandleBack(int messageId)
        {
            var secondTab = SecondTabs.FirstOrDefault(t => t.TabId == messageId);
            if (secondTab == null) return;

            var firstTab = FirstTabs.FirstOrDefault(t => t.User.UserId == secondTab.User.UserId);
            if (firstTab != null)
            {
                // No new messages — just mark as read
                string newDescription = $"✔️{firstTab.User.FirstName} {firstTab.User.LastName}: Прочитано";
                if (firstTab.Description != newDescription)
                {
                    firstTab.Description = newDescription;
                    try
                    {
                        await botClient.EditMessageText(TelegramService.GetTelegramId(), firstTab.TabId, firstTab.Description, replyMarkup: firstTab.GetInlineKeyboard());
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Ошибка при редактировании FirstTab на Прочитано: {ex.Message}");
                    }
                }
            }

            await secondTab.DeleteAllBotMessages();

            try
            {
                await botClient.DeleteMessage(TelegramService.GetTelegramId(), messageId);

                SecondTabs.Remove(secondTab);
                secondTab.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при возврате к FirstTab: {ex.Message}");
            }

            
            await FlushPendingFirstTabs();
        }


        // ──────────────────────────────────────────────────────────────────
        //  Attachment number selection flow
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Handles "Посмотреть вложения" — asks the user to enter the message number
        /// whose attachments they want to view.
        /// </summary>
        // Вспомогательная функция: рекурсивно проверяет, есть ли вложения в сообщении,
        // включая все вложенные ForwardedMessages и ReplyMessage
        private static bool HasAnyAttachmentsRecursive(VkNet.Model.Message message)
        {
            if (message == null) return false;

            // Проверяем вложения в самом сообщении
            if (message.Attachments != null && message.Attachments.Count > 0)
                return true;

            // Рекурсивно проверяем ответное сообщение
            if (message.ReplyMessage != null && HasAnyAttachmentsRecursive(message.ReplyMessage))
                return true;

            // Рекурсивно проверяем все пересланные сообщения
            if (message.ForwardedMessages != null)
            {
                foreach (var fwd in message.ForwardedMessages)
                {
                    if (HasAnyAttachmentsRecursive(fwd))
                        return true;
                }
            }

            return false;
        }

        // Вспомогательная функция: рекурсивно собирает все вложения из сообщения
        // и всех вложенных (ForwardedMessages, ReplyMessage)
        private static void CollectAllAttachmentsRecursive(VkNet.Model.Message message, List<VkNet.Model.Attachment> result)
        {
            if (message == null) return;

            // Добавляем вложения самого сообщения
            if (message.Attachments != null)
                result.AddRange(message.Attachments);

            // Обрабатываем ответное сообщение
            if (message.ReplyMessage != null)
                CollectAllAttachmentsRecursive(message.ReplyMessage, result);

            // Обрабатываем все пересланные сообщения
            if (message.ForwardedMessages != null)
            {
                foreach (var fwd in message.ForwardedMessages)
                    CollectAllAttachmentsRecursive(fwd, result);
            }
        }

        internal static async Task HandleCheckAttachments(int messageId)
        {
            var secondTab = SecondTabs.FirstOrDefault(t => t.TabId == messageId);
            if (secondTab == null) return;

            // Build list of messages that have attachments anywhere in their tree
            var messagesWithAttachments = secondTab.User.Messages
                .Select((m, idx) => new { Message = m, Number = idx + 1 })
                .Where(x => HasAnyAttachmentsRecursive(x.Message))
                .ToList();

            if (messagesWithAttachments.Count == 0)
            {
                var noAttMsg = await botClient.SendMessage(
                    chatId: TelegramService.GetTelegramId(),
                    text: "Вложений нет."
                );
                secondTab.BotMessageIds.Add(noAttMsg.MessageId);
                return;
            }

            // Build inline keyboard: one button per message that has attachments
            var buttonRows = messagesWithAttachments
                .Select(x =>
                {
                    string preview = string.IsNullOrWhiteSpace(x.Message.Text)
                        ? $"Сообщение {x.Number}"
                        : $"№{x.Number}: {x.Message.Text.Substring(0, Math.Min(20, x.Message.Text.Length))}…";

                    return new[]
                    {
                InlineKeyboardButton.WithCallbackData(
                    preview,
                    $"words:show_att:{messageId}:{x.Number - 1}" // zero-based index
                )
                    };
                })
                .ToList();

            // Add cancel button
            buttonRows.Add(new[]
            {
        InlineKeyboardButton.WithCallbackData("Отмена", $"words:back_attachments:{messageId}")
    });

            var keyboard = new InlineKeyboardMarkup(buttonRows);

            try
            {
                var promptMsg = await botClient.SendMessage(
                    chatId: TelegramService.GetTelegramId(),
                    text: "📎 Выберите сообщение, вложения которого хотите посмотреть:",
                    replyMarkup: keyboard
                );
                secondTab.BotMessageIds.Add(promptMsg.MessageId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при запросе номера сообщения: {ex.Message}");
            }
        }

        internal static async Task HandleShowAttachmentForIndex(int tabMessageId, int messageIndex)
        {
            var secondTab = SecondTabs.FirstOrDefault(t => t.TabId == tabMessageId);
            if (secondTab == null) return;

            // Delete the selection prompt
            await secondTab.DeleteAllBotMessages();

            if (messageIndex < 0 || messageIndex >= secondTab.User.Messages.Count)
            {
                var errMsg = await botClient.SendMessage(
                    chatId: TelegramService.GetTelegramId(),
                    text: $"⚠️ Сообщение с номером {messageIndex + 1} не найдено."
                );
                secondTab.BotMessageIds.Add(errMsg.MessageId);
                return;
            }

            var targetMessage = secondTab.User.Messages[messageIndex];

            // Рекурсивно собираем все вложения
            var allAttachments = new List<VkNet.Model.Attachment>();
            CollectAllAttachmentsRecursive(targetMessage, allAttachments);

            if (allAttachments.Count == 0)
            {
                var noAttMsg = await botClient.SendMessage(
                    chatId: TelegramService.GetTelegramId(),
                    text: $"В сообщении №{messageIndex + 1} нет вложений (включая пересланные и ответные)."
                );
                secondTab.BotMessageIds.Add(noAttMsg.MessageId);
                return;
            }

            foreach (var att in allAttachments)
            {
                try
                {
                    global::Telegram.Bot.Types.Message? sentMsg = null;

                    switch (att.Type.Name)
                    {
                        case "Photo":
                            {
                                var photo = att.Instance as VkNet.Model.Photo;
                                var url = photo?.Sizes?.OrderByDescending(s => s.Width).FirstOrDefault()?.Url?.ToString();
                                if (!string.IsNullOrEmpty(url))
                                {
                                    sentMsg = await botClient.SendPhoto(
                                        chatId: TelegramService.GetTelegramId(),
                                        photo: new InputFileUrl(url),
                                        caption: photo?.Text
                                    );
                                }
                            }
                            break;

                        case "Video":
                            {
                                var video = att.Instance as VkNet.Model.Video;
                                // Попытка получить прямую ссылку на mp4-файл
                                string? videoUrl = video?.Files?.Mp4_1080?.ToString()
                                                ?? video?.Files?.Mp4_720?.ToString()
                                                ?? video?.Files?.Mp4_480?.ToString()
                                                ?? video?.Files?.Mp4_360?.ToString()
                                                ?? video?.Files?.Mp4_240?.ToString()
                                                ?? video?.Files?.External?.ToString();

                                if (!string.IsNullOrEmpty(videoUrl))
                                {
                                    sentMsg = await botClient.SendVideo(
                                        chatId: TelegramService.GetTelegramId(),
                                        video: new InputFileUrl(videoUrl),
                                        caption: $"🎬 {video?.Title}\n{video?.Description}".Trim()
                                    );
                                }
                                else
                                {
                                    // Отправляем ссылку на видео
                                    string link = video?.Id != null && video?.OwnerId != null
                                        ? $"https://vk.com/video{video.OwnerId}_{video.Id}"
                                        : "недоступно";
                                    sentMsg = await botClient.SendMessage(
                                        chatId: TelegramService.GetTelegramId(),
                                        text: $"🎥 Видео: {video?.Title ?? "без названия"}\n{link}",
                                        linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true }
                                    );
                                }
                            }
                            break;

                        case "Document":
                            {
                                var doc = att.Instance as VkNet.Model.Document;
                                if (doc?.Uri != null)
                                {
                                    sentMsg = await botClient.SendDocument(
                                        chatId: TelegramService.GetTelegramId(),
                                        document: new InputFileUrl(doc.Uri.ToString())
                                    );
                                }
                                else
                                {
                                    sentMsg = await botClient.SendMessage(
                                        chatId: TelegramService.GetTelegramId(),
                                        text: $"📄 Документ: {doc?.Title ?? "без названия"}"
                                    );
                                }
                            }
                            break;

                        case "Audio":
                            {
                                var audio = att.Instance as VkNet.Model.Audio;
                                string artist = audio?.Artist ?? "Неизвестный исполнитель";
                                string title = audio?.Title ?? "Без названия";
                                // VK Audio не всегда отдаёт прямую ссылку в API. Если есть Url – отправим как аудиофайл.
                                if (audio?.Url != null)
                                {
                                    sentMsg = await botClient.SendAudio(
                                        chatId: TelegramService.GetTelegramId(),
                                        audio: new InputFileUrl(audio.Url.ToString()),
                                        title: title,
                                        performer: artist
                                    );
                                }
                                else
                                {
                                    sentMsg = await botClient.SendMessage(
                                        chatId: TelegramService.GetTelegramId(),
                                        text: $"🎵 Аудиозапись: {artist} - {title}"
                                    );
                                }
                            }
                            break;

                        case "AudioMessage":
                            {
                                var audioMsg = att.Instance as VkNet.Model.AudioMessage;
                                if (audioMsg?.LinkMp3 != null)
                                {
                                    sentMsg = await botClient.SendVoice(
                                        chatId: TelegramService.GetTelegramId(),
                                        voice: new InputFileUrl(audioMsg.LinkMp3.ToString())
                                    );
                                }
                                else
                                {
                                    sentMsg = await botClient.SendMessage(
                                        chatId: TelegramService.GetTelegramId(),
                                        text: $"🎤 Голосовое сообщение (длительность: {audioMsg?.Duration} сек.)"
                                    );
                                }
                            }
                            break;

                        case "Sticker":
                            {
                                var sticker = att.Instance as VkNet.Model.Sticker;
                                // Стикер можно отправить как фото (берем максимальный размер)
                                var url = sticker?.Images?.OrderByDescending(i => i.Width).FirstOrDefault()?.Url?.ToString();
                                if (!string.IsNullOrEmpty(url))
                                {
                                    sentMsg = await botClient.SendPhoto(
                                        chatId: TelegramService.GetTelegramId(),
                                        photo: new InputFileUrl(url)
                                    );
                                }
                                else
                                {
                                    sentMsg = await botClient.SendMessage(
                                        chatId: TelegramService.GetTelegramId(),
                                        text: $"🎭 Стикер (ID: {sticker?.Id})"
                                    );
                                }
                            }
                            break;

                        case "Graffiti":
                            {
                                var graffiti = att.Instance as VkNet.Model.Graffiti;
                                var url = graffiti?.Photo586?.ToString() ?? graffiti?.Photo200?.ToString();
                                if (!string.IsNullOrEmpty(url))
                                {
                                    sentMsg = await botClient.SendPhoto(
                                        chatId: TelegramService.GetTelegramId(),
                                        photo: new InputFileUrl(url),
                                        caption: "🎨 Граффити"
                                    );
                                }
                                else
                                {
                                    sentMsg = await botClient.SendMessage(
                                        chatId: TelegramService.GetTelegramId(),
                                        text: "🎨 Граффити"
                                    );
                                }
                            }
                            break;

                        case "Link":
                            {
                                var link = att.Instance as VkNet.Model.Link;
                                string linkInfo = $"🔗 Ссылка: {link?.Title ?? link?.Uri?.ToString() ?? "без названия"}";
                                if (link?.Uri != null)
                                    linkInfo += $"\n{link.Uri}";
                                sentMsg = await botClient.SendMessage(
                                    chatId: TelegramService.GetTelegramId(),
                                    text: linkInfo,
                                    linkPreviewOptions: false
                                );
                            }
                            break;

                        case "Note":
                            {
                                var note = att.Instance as VkNet.Model.Note;
                                sentMsg = await botClient.SendMessage(
                                    chatId: TelegramService.GetTelegramId(),
                                    text: $"📝 Заметка:\n{note?.Text ?? "без текста"}"
                                );
                            }
                            break;

                        case "Poll":
                            {
                                var poll = att.Instance as VkNet.Model.Poll;
                                var sb = new StringBuilder();
                                sb.AppendLine($"📊 Опрос: {poll?.Question ?? "Без вопроса"}");
                                if (poll?.Answers != null)
                                {
                                    foreach (var answer in poll.Answers)
                                    {
                                        sb.AppendLine($"- {answer.Text} (голосов: {answer.Votes})");
                                    }
                                }
                                sentMsg = await botClient.SendMessage(
                                    chatId: TelegramService.GetTelegramId(),
                                    text: sb.ToString()
                                );
                            }
                            break;

                        case "Gift":
                            {
                                var gift = att.Instance as VkNet.Model.Gift;
                                sentMsg = await botClient.SendMessage(
                                    chatId: TelegramService.GetTelegramId(),
                                    text: $"🎁 Подарок: {gift?.Id}"
                                );
                            }
                            break;

                        case "Wall":
                            {
                                var wallPost = att.Instance as VkNet.Model.Wall;
                                string postInfo = $"📰 Запись со стены";
                                if (wallPost?.Id != null && wallPost?.OwnerId != null)
                                    postInfo += $"\nhttps://vk.com/wall{wallPost.OwnerId}_{wallPost.Id}";
                                sentMsg = await botClient.SendMessage(
                                    chatId: TelegramService.GetTelegramId(),
                                    text: postInfo
                                );
                            }
                            break;

                        case "Market":
                            {
                                var market = att.Instance as VkNet.Model.Market;
                                sentMsg = await botClient.SendMessage(
                                    chatId: TelegramService.GetTelegramId(),
                                    text: $"🛒 Товар: {market?.Title ?? "без названия"}"
                                );
                            }
                            break;

                        case "Album":
                            {
                                var album = att.Instance as VkNet.Model.Album;
                                sentMsg = await botClient.SendMessage(
                                    chatId: TelegramService.GetTelegramId(),
                                    text: $"🖼️ Альбом: {album?.Title ?? "без названия"} ({album?.Size} фото)"
                                );
                            }
                            break;

                        default:
                            sentMsg = await botClient.SendMessage(
                                chatId: TelegramService.GetTelegramId(),
                                text: $"📎 Вложение: {att.Type.Name}"
                            );
                            break;
                    }

                    if (sentMsg != null)
                        secondTab.BotMessageIds.Add(sentMsg.MessageId);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка при отправке вложения типа {att.Type.Name}: {ex.Message}");
                }
            }

            // Send "back" button below attachments
            try
            {
                var backKeyboard = new InlineKeyboardMarkup(new[]
                {
            new[] { InlineKeyboardButton.WithCallbackData("Назад", $"words:back_attachments:{tabMessageId}") }
        });

                string sourceInfo = "";
                if (targetMessage.ForwardedMessages != null && targetMessage.ForwardedMessages.Count > 0)
                    sourceInfo += " (включая пересланные)";
                if (targetMessage.ReplyMessage != null)
                    sourceInfo += " (включая ответное)";

                var backMsg = await botClient.SendMessage(
                    chatId: TelegramService.GetTelegramId(),
                    text: $"⬆️ Вложения сообщения №{messageIndex + 1}{sourceInfo}",
                    replyMarkup: backKeyboard
                );
                secondTab.BotMessageIds.Add(backMsg.MessageId);
                secondTab.AdditionalMessageId = backMsg.MessageId;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при отправке кнопки назад: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles "Назад" in the attachment view — deletes all attachment messages.
        /// </summary>
        internal static async Task HandleBackFromAttachments(int messageId)
        {
            var secondTab = SecondTabs.FirstOrDefault(t => t.TabId == messageId);
            if (secondTab == null) return;

            await secondTab.DeleteAllBotMessages();
        }
    }
}
