using System.Runtime.InteropServices;
using vk_forwarder.Telegram;

namespace vk_forwarder
{
    class Program
    {
        static async Task Main(string[] args)
        {

            var cts = new CancellationTokenSource();

            // Регистрируем обработчики ДО запуска любых сервисов
            // SIGINT (Ctrl+C)
            Console.CancelKeyPress += (_, e) => {
                e.Cancel = true;
                cts.Cancel();
            };

            // SIGTERM (systemd stop/restart) — на Linux CancelKeyPress его не ловит
            // ВАЖНО: сохраняем в переменную — иначе GC соберёт объект и регистрация отменится
            var sigtermReg = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ =>
            {
                Console.WriteLine("Получен SIGTERM, останавливаем бот...");
                cts.Cancel();
            });

            // Глобальная защита: unobserved исключения из Task.Run не убивают процесс
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Console.WriteLine($"[UnobservedTask] {e.Exception.GetBaseException().Message}");
                e.SetObserved();
            };

            // Любое необработанное исключение в потоке — логируем и не падаем
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Console.WriteLine($"[UnhandledException] {(e.ExceptionObject as Exception)?.Message}");
            };

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

            try
            {
                Console.WriteLine("[Main] Начинаем слушать VK LongPoll...");
                await vkService.StartListening(cts.Token);
                Console.WriteLine("[Main] StartListening завершился штатно.");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[Main] Бот остановлен через отмену токена. IsCancellationRequested={cts.IsCancellationRequested}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Main] Необработанное исключение: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                sigtermReg.Dispose();
                Console.WriteLine("[Main] Выход из Main.");
            }

        }

        static void PrintBanner()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(@"
            ╔═════════════════════════════════════════════════════╗
            ║          VK Group ↔ Telegram Bridge v1.9            ║
            ║   Пересылка сообщений между группой ВК и Telegram   ║
            ╚════════════╗                          ╔═════════════╝
                         ║    [\..Бот запущен../]   ║                   
                         ╚══════════════════════════╝ ");
            Console.ResetColor();
        }
    }
}

