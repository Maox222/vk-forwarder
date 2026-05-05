using System.Text.Json;
using System.Threading.Channels;
using VkNet;
using VkNet.Enums.StringEnums;
using VkNet.Model;

namespace vk_forwarder
{
    // ──────────────────────────────────────────────────────────────────────────
    //  VK Keyboard builder helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fluent builder for VK Bot keyboards (JSON format required by VK API).
    /// Usage:
    ///   var kb = new VkKeyboardBuilder()
    ///       .AddButton("Привет",  "hello",  VkButtonColor.Positive)
    ///       .NewRow()
    ///       .AddButton("О нас",   "about",  VkButtonColor.Primary)
    ///       .Build();
    /// </summary>
    public class VkKeyboardBuilder
    {
        private readonly List<List<object>> _rows = new() { new List<object>() };
        private bool _oneTime = true;
        private bool _inline  = false;

        public VkKeyboardBuilder OneTime(bool value = true)  { _oneTime = value; return this; }
        public VkKeyboardBuilder Inline(bool value = true)   { _inline  = value; return this; }

        /// <summary>Adds a text button to the current row.</summary>
        public VkKeyboardBuilder AddButton(string label, string payload, VkButtonColor color = VkButtonColor.Default)
        {
            // VK API requires payload to be a JSON-encoded string, e.g. "{\"button\":\"start\"}"
            // Passing a plain string like "start" causes: "button has invalid payload"
            var payloadJson = JsonSerializer.Serialize(new { button = payload });

            _rows.Last().Add(new
            {
                action = new { type = "text", label, payload = payloadJson },
                color  = color.ToString().ToLower()
            });
            return this;
        }

        public VkKeyboardBuilder AddLinkButton(string label, string url)
        {
            _rows.Last().Add(new
            {
                action = new { type = "open_link", label, link = url }
            });
            return this;
        }

        /// <summary>Starts a new row of buttons.</summary>
        public VkKeyboardBuilder NewRow()
        {
            _rows.Add(new List<object>());
            return this;
        }

        /// <summary>Serialises the keyboard to the JSON string expected by VK API.</summary>
        public string Build()
        {
            return JsonSerializer.Serialize(new
            {
                one_time = _oneTime,
                inline   = _inline,
                buttons  = _rows
            });
        }
    }

    public enum VkButtonColor { Default, Primary, Positive, Negative, Secondary }

    // ──────────────────────────────────────────────────────────────────────────
    //  Button handler context
    // ──────────────────────────────────────────────────────────────────────────

    public class VkButtonContext
    {
        public VkNet.Model.Message Message { get; init; }
        public string              Payload { get; init; }
        public long                PeerId  { get; init; }
        public VkApi               Api     { get; init; }
        public long?               GroupId { get; init; }
        public string              FirstName { get; init; } = "";
        public string?             Photo { get; set; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Service
    // ──────────────────────────────────────────────────────────────────────────

    internal class vkService
    {
        private static long? GroupId;
        private static VkApi api;

        private static readonly Channel<VkNet.Model.Message> _messageQueue =
            Channel.CreateUnbounded<VkNet.Model.Message>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

        private static readonly Dictionary<string, Func<VkButtonContext, Task>> _buttonHandlers
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Texts loaded from vk_responses.json. Key = payload, Value = message text.
        /// </summary>
        private static Dictionary<string, VkResponse> _responses = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Keyboard JSON strings built from vk_keyboards.json. Key = payload.</summary>
        private static Dictionary<string, string> _keyboards = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether VK button support is enabled (from vk_keyboards.json).</summary>
        private static bool _buttonsEnabled = false;

        public class VkResponse
        {

            public string Text { get; set; } = "";
            public List<string>? Photo { get; set; }
            public List<string>? Video { get; set; }
        }

        // ─────────────────────────────────────────────────────────────────────

        internal static void StartService(string vkToken, long? groupId)
        {
            GroupId = groupId;
            api = new VkApi();
            api.Authorize(new ApiAuthParams
            {
                AccessToken = vkToken
            });

            LoadResponses();
            LoadKeyboards();
        }

        // ──────────────────────────────────────────────────────────────────────
        //  JSON responses loader
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Loads vk_responses.json from the same directory as the executable.
        /// The file is a flat JSON object: { "payload_key": "message text", ... }
        /// If the file is missing, an empty dictionary is used and a warning is printed.
        /// </summary>
        private static void LoadResponses()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "vk_responses.json");
            if (!File.Exists(path)) { Console.WriteLine($"[VK] vk_responses.json не найден"); return; }

            try
            {
                var json = File.ReadAllText(path);
                _responses = JsonSerializer.Deserialize<Dictionary<string, VkResponse>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new();
                Console.WriteLine($"[VK] Загружено {_responses.Count} ответов из vk_responses.json");
            }
            catch (Exception ex) { Console.WriteLine($"[VK] Ошибка: {ex.Message}"); }
        }

        /// <summary>
        /// Returns the response text for the given payload key.
        /// Falls back to a generic message if the key is missing.
        /// </summary>
        private static VkResponse GetResponse(string payload, string? firstName = null)
        {
            var source = _responses.TryGetValue(payload, out var r) ? r
                         : new VkResponse { Text = $"[Ответ для «{payload}» не найден]" };

            // Always return a copy — never mutate the cached original
            var resp = new VkResponse
            {
                Text  = source.Text,
                Photo = source.Photo,
                Video = source.Video,
            };

            if (!string.IsNullOrEmpty(firstName))
                resp.Text = resp.Text.Replace("{firstName}", firstName);

            return resp;
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Keyboards loader
        // ──────────────────────────────────────────────────────────────────────

        // JSON model for vk_keyboards.json
        private class KeyboardConfig
        {
            public bool Enabled { get; set; } = false;
            public List<MenuConfig> Menus { get; set; } = new();
        }
        private class MenuConfig
        {
            public string Payload { get; set; } = "";
            public bool OneTime { get; set; } = true;
            public List<List<ButtonConfig>> Rows { get; set; } = new();
        }
        private class ButtonConfig
        {
            public string Label   { get; set; } = "";
            public string? Payload { get; set; }   // text button
            public string? Url     { get; set; }   // link button
            public string  Color   { get; set; } = "default";
        }

        /// <summary>
        /// Loads vk_keyboards.json, builds keyboard JSON strings and registers
        /// a handler for every menu payload. If enabled=false, clears all handlers.
        /// </summary>
        private static void LoadKeyboards()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "vk_keyboards.json");
            if (!File.Exists(path))
            {
                Console.WriteLine("[VK] vk_keyboards.json не найден — кнопки отключены.");
                _buttonsEnabled = false;
                return;
            }

            try
            {
                var json = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize<KeyboardConfig>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (config == null || !config.Enabled)
                {
                    Console.WriteLine("[VK] Кнопки отключены (enabled: false).");
                    _buttonsEnabled = false;
                    _buttonHandlers.Clear();
                    return;
                }

                _buttonsEnabled = true;
                _buttonHandlers.Clear();
                _keyboards.Clear();

                // Build keyboard JSON for each menu and register its handler
                foreach (var menu in config.Menus)
                {
                    var builder = new VkKeyboardBuilder().OneTime(menu.OneTime);

                    for (int i = 0; i < menu.Rows.Count; i++)
                    {
                        if (i > 0) builder.NewRow();
                        foreach (var btn in menu.Rows[i])
                        {
                            if (!string.IsNullOrEmpty(btn.Url))
                                builder.AddLinkButton(btn.Label, btn.Url);
                            else if (!string.IsNullOrEmpty(btn.Payload))
                            {
                                var color = btn.Color.ToLower() switch
                                {
                                    "primary"  => VkButtonColor.Primary,
                                    "positive" => VkButtonColor.Positive,
                                    "negative" => VkButtonColor.Negative,
                                    _          => VkButtonColor.Default
                                };
                                builder.AddButton(btn.Label, btn.Payload, color);
                            }
                        }
                    }

                    var keyboardJson = builder.Build();
                    var payloadKey   = menu.Payload; // capture for closure

                    _keyboards[payloadKey] = keyboardJson;

                    _buttonHandlers[payloadKey] = async ctx =>
                    {
                        var kb   = _keyboards.TryGetValue(payloadKey, out var k) ? k : null;
                        var resp = GetResponse(payloadKey, payloadKey == "start" ? ctx.FirstName : null);
                        await SendMessageAsync(ctx.PeerId, resp.Text, kb, photos: resp.Photo, videos: resp.Video);
                    };
                }

                Console.WriteLine($"[VK] Загружено {config.Menus.Count} меню из vk_keyboards.json.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VK] Ошибка при загрузке vk_keyboards.json: {ex.Message}");
                _buttonsEnabled = false;
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Sending helpers
        // ──────────────────────────────────────────────────────────────────────

        public static async Task SendMessageAsync(long peerId, string text, string? keyboardJson = null, List<string>? photos = null, List<string>? videos = null)
        {
            await Task.Run(() =>
            {
                var p = new MessagesSendParams
                {
                    PeerId = peerId,
                    RandomId = new Random().Next(),
                    Message = text,
                };

                if (!string.IsNullOrEmpty(keyboardJson))
                    p.Keyboard = Newtonsoft.Json.JsonConvert.DeserializeObject<VkNet.Model.MessageKeyboard>(keyboardJson);

                var attachments = new List<MediaAttachment>();

                if (photos != null)
                {
                    foreach (var photo in photos)
                    {
                        var parts = photo.Replace("photo", "").Split('_');
                        attachments.Add(new VkNet.Model.Photo
                        {
                            OwnerId = long.Parse(parts[0]),
                            Id = long.Parse(parts[1])
                        });
                    }
                }

                if (videos != null)
                {
                    foreach (var video in videos)
                    {
                        var parts = video.Replace("video", "").Split('_');
                        attachments.Add(new VkNet.Model.Video
                        {
                            OwnerId = long.Parse(parts[0]),
                            Id = long.Parse(parts[1])
                        });
                    }
                }

                if (attachments.Count > 0) p.Attachments = attachments;

                try
                {
                    api.Messages.Send(p);
                }
                catch { }
            });
        }


        // ──────────────────────────────────────────────────────────────────────
        //  Timer After Buttons
        // ──────────────────────────────────────────────────────────────────────
        private static void ResetInactivityTimer(VkUser user)
        {
            if (!user.WaitingAfterButton) return;

            // Cancel and dispose any existing timer for this user
            user.InactivityTimer?.Cancel();
            user.InactivityTimer?.Dispose();

            var cts = new CancellationTokenSource();
            user.InactivityTimer = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(30), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Timer was reset or stopped (user pressed a button or wrote a message) — exit silently
                    return;
                }

                // Timer fired — user was inactive. Send message and clean up.
                try
                {
                    await SendMessageAsync(user.PeerId, GetResponse("inactivity").Text);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[VK Timer] Ошибка при отправке inactivity для peerId={user.PeerId}: {ex.Message}");
                }
                finally
                {
                    user.InactivityTimer = null;
                    user.WaitingAfterButton = false;
                }
            });
        }

        private static void StopInactivityTimer(VkUser user)
        {
            user.InactivityTimer?.Cancel();
            user.InactivityTimer?.Dispose();
            user.InactivityTimer = null;
        }

        // ──────────────────────────────────────────────────────────────────────
        //  LongPoll loop
        // ──────────────────────────────────────────────────────────────────────

        internal static async Task StartListening(CancellationToken ct = default)
        {
            var workerTask = Task.Run(() => ProcessMessageWorker(ct), ct);

            var longPollServer = api.Groups.GetLongPollServer(Convert.ToUInt64(GroupId));

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var history = api.Groups.GetBotsLongPollHistory(new BotsLongPollHistoryParams
                    {
                        Server = longPollServer.Server,
                        Ts     = longPollServer.Ts,
                        Key    = longPollServer.Key,
                        Wait   = 25
                    });

                    longPollServer.Ts = history.Ts;

                    if (history.Updates == null) continue;

                    foreach (var update in history.Updates)
                    {
                        if (update.Type.Value == GroupUpdateType.MessageNew)
                        {
                            var message = (update.Instance as MessageNew)?.Message;
                            if (message?.FromId != null)
                            {
                                _messageQueue.Writer.TryWrite(message);
                            }
                        }
                        else if (update.Type.Value == GroupUpdateType.MessageEdit)
                        {
                            var message = update.Instance as VkNet.Model.Message;
                            if (message?.Text != null && message.FromId != null)
                            {
                                _messageQueue.Writer.TryWrite(message);
                            }
                        }
                        else if (update.Type.Value == GroupUpdateType.MessageReply)
                        {
                            var message = update.Instance as VkNet.Model.Message;
                            if (message?.Text != null && message.AdminAuthorId != null
                                && !IsBotResponse(message.Text))
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
                    longPollServer.Ts  = fresh.Ts;
                }
                catch (VkNet.Exception.LongPollInfoLostException)
                {
                    Console.WriteLine("[VK LongPoll] История потеряна, переподключаемся...");
                    longPollServer = api.Groups.GetLongPollServer(Convert.ToUInt64(GroupId));
                }
                catch (VkNet.Exception.LongPollOutdateException)
                {
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
            }

            _messageQueue.Writer.Complete();
            await workerTask;
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Message worker
        // ──────────────────────────────────────────────────────────────────────

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
            var text   = message.Text ?? string.Empty;
            if (string.IsNullOrEmpty(text) && message.FromId == null) return;

            var existingTab = MessageDispatcher.FirstTabs.FirstOrDefault(tbs => tbs.User.PeerId == peerId);

            // ── 1. Button payload ─────────────────────────────────────────────
            var payload = _buttonsEnabled
                ? (ExtractPayload(message.Payload) ?? (text != null && text.Equals("Начать", StringComparison.OrdinalIgnoreCase) ? "start" : string.Empty))
                : string.Empty;
            bool isButton = !string.IsNullOrEmpty(payload) && _buttonHandlers.ContainsKey(payload);

            // Fetch VK user info once if tab doesn't exist yet — reused below
            VkNet.Model.User? vkApiUser = existingTab == null && message.AdminAuthorId == null
                ? api.Users.Get(new long[] { peerId }).FirstOrDefault()
                : null;

            if (isButton)
            {
                string firstName = existingTab?.User.FirstName
                    ?? vkApiUser?.FirstName
                    ?? "";

                await _buttonHandlers[payload](new VkButtonContext
                {
                    Message   = message,
                    Payload   = payload,
                    PeerId    = peerId,
                    Api       = api,
                    GroupId   = GroupId,
                    FirstName = firstName,
                });
            }

            // ── 2. Notify operator in Telegram ────────────────────────────────
            if (existingTab == null && message.AdminAuthorId == null)
            {
                // New user — create tab regardless of whether it's a button or a real message
                FirstTab firstTab = new FirstTab()
                {
                    User = new VkUser(peerId, vkApiUser?.FirstName ?? "Неизвестный", vkApiUser?.LastName ?? "")
                };
                await firstTab.User.AddMessage(message);
                existingTab = firstTab;
            }
            else if (existingTab != null)
            {
                if (existingTab.User.Messages.Any(msg => msg.Id == message.Id))
                    await existingTab.User.EditMessage(message);
                else
                    await existingTab.User.AddMessage(message); // button press also shows up in tab
            }

            // ── 3. Start or stop inactivity timer ────────────────────────────
            if (existingTab != null)
            {
                if (isButton)
                {
                    existingTab.User.WaitingAfterButton = true;
                    ResetInactivityTimer(existingTab.User);
                }
                else
                {
                    existingTab.User.WaitingAfterButton = false;
                    StopInactivityTimer(existingTab.User);
                }
            }
        }


        // ──────────────────────────────────────────────────────────────────────
        //  Bot response filter
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns true if <paramref name="text"/> exactly matches any value
        /// stored in _responses (i.e. the message was sent by the bot itself).
        /// Such MessageReply updates are silently dropped and never forwarded to Telegram.
        /// </summary>
        private static bool IsBotResponse(string text)
            => _responses.Values.Any(v => v.Text == text);

        // ──────────────────────────────────────────────────────────────────────
        //  Payload helper
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// VK sends payload exactly as we serialised it in AddButton: {"button":"start"}
        /// Extracts the value of the "button" key (or other known keys for forward compatibility).
        /// Returns the normalised lowercase payload string, or null if none.
        /// </summary>
        private static string? ExtractPayload(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var trimmed = raw.Trim();

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;

                foreach (var key in new[] { "button", "command", "payload", "action" })
                {
                    if (root.TryGetProperty(key, out var val))
                        return val.GetString()?.ToLower();
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Accessors
        // ──────────────────────────────────────────────────────────────────────

        internal static VkApi GetVkApi()   => api;
        internal static long? GetGroupId() => GroupId;
    }
}
