using Microsoft.Extensions.Configuration;
using vk_forwarder.Telegram;

namespace vk_forwarder
{
    class Program
    {
        static async Task Main(string[] args)
        {

            try
            {
                var vkToken = Environment.GetEnvironmentVariable("VK_GROUP_TOKEN");
                var vkId = Convert.ToInt64(Environment.GetEnvironmentVariable("VK_GROUP_ID"));

                if (string.IsNullOrEmpty(vkToken) || vkId == 0)
                {
                    throw new Exception("VK_GROUP_TOKEN и/или VK_GROUP_ID не найдены в файле конфигурации службы");
                }
                vkService.StartService(vkToken, vkId);

                var tgToken = Environment.GetEnvironmentVariable("TG_BOT_TOKEN");
                var tgId = Convert.ToInt64(Environment.GetEnvironmentVariable("TG_ADMIN_ID"));

                if (string.IsNullOrEmpty(tgToken) || tgId == null)
                {
                    throw new Exception("TG_BOT_TOKEN и/или TG_ADMIN_ID не найдены в файле конфигурации службы");
                }

                TelegramService.StartService(tgToken, tgId);

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при запуске vkSevice или TelegramService: {ex.Message}");
                return;
            }

            PrintBanner();

            var cts = new CancellationTokenSource();

            // Обработка Ctrl+C и сигналов systemd (SIGTERM)
            Console.CancelKeyPress += (_, e) => {
                e.Cancel = true;
                cts.Cancel();
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

            try
            {
                await vkService.StartListening(cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Бот остановлен.");
            }

        }

        static void PrintBanner()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(@"
            ╔═════════════════════════════════════════════════════╗
            ║          VK Group ↔ Telegram Bridge v1.4            ║
            ║   Пересылка сообщений между группой ВК и Telegram   ║
            ╚════════════╗                          ╔═════════════╝
                         ║    [\..Бот запущен../]   ║                   
                         ╚══════════════════════════╝ ");
            Console.ResetColor();
        }
    }
}

