using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MoneyGenerator_v5.Services;
using MoneyGenerator_v5.Strategies;
using MoneyGenerator_v5.ViewModels;
using MoneyGenerator_v5.Views;
using System;
using System.Windows;
using Tinkoff.InvestApi;

namespace MoneyGenerator_v5
{
    public partial class App : Application
    {
        public static IServiceProvider ServiceProvider { get; private set; }

        public App()
        {
            // Уберите Startup инициализацию отсюда
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Создаем и настраиваем DI-контейнер
            var services = new ServiceCollection();
            ConfigureServices(services);

            ServiceProvider = services.BuildServiceProvider();

            // Создаем и показываем главное окно
            var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }

        // Регистрация сервисов DI
        private void ConfigureServices(IServiceCollection services)
        {
            // Регистрируем логгирование
            services.AddLogging(configure =>
                configure.AddConsole()
                         .AddDebug()
                         .SetMinimumLevel(LogLevel.Information));

            // ВАЖНО: Регистрируем TokenManager ПЕРВЫМ, чтобы другие сервисы могли его использовать
            services.AddSingleton<TokenManager>();

            // ConnectionManager регистрируем как синглтон
            services.AddSingleton<ConnectionManager>();


           /* // ✅ Регистрируем TransactionsService (теперь у него есть IProvirerService)
            services.AddSingleton<TransactionsService>();
*/



            // Создаем и регистрируем клиент Tinkoff API
            services.AddSingleton<InvestApiClient>(serviceProvider =>
            {
                var tokenManager = serviceProvider.GetRequiredService<TokenManager>();
                var logger = serviceProvider.GetService<ILogger<App>>();

                // Получаем токен - используем правильный метод
                var tokens = tokenManager.LoadProviderTokens("Тинькофф");
                string accessToken = tokens.SandboxToken; // Используем сандбокс токен

                if (string.IsNullOrEmpty(accessToken))
                {
                    logger?.LogError("Tinkoff API token not found");
                    throw new InvalidOperationException("Tinkoff API token not found");
                }

                logger?.LogInformation("Creating Tinkoff API client for sandbox");

                // Используем правильную фабрику для создания клиента
                return InvestApiClientFactory.Create(accessToken);
            });



            // Регистрируем TinkoffApiService (как конкретную реализацию, но IProvirerService уже зарегистрирован)
            services.AddSingleton<TinkoffApiService>(serviceProvider =>
            {
                var logger = serviceProvider.GetRequiredService<ILogger<TinkoffApiService>>();
                var tokenManager = serviceProvider.GetRequiredService<TokenManager>();
                var connectionManager = serviceProvider.GetRequiredService<ConnectionManager>();

                return new TinkoffApiService(logger, tokenManager, connectionManager);
            });


            // Регистрируем другие сервисы провайдеров
            services.AddSingleton<FinamApiService>();
            services.AddSingleton<AlorApiService>();

            // Регистрируем фабрику, которая будет выбирать нужную реализацию
            services.AddSingleton<Func<string, IProvirerService>>(serviceProvider => providerName =>
            {
                return providerName switch
                {
                    "Тинькофф" => serviceProvider.GetRequiredService<TinkoffApiService>(),
                    "Финам" => serviceProvider.GetRequiredService<FinamApiService>(),
                    "Алор" => serviceProvider.GetRequiredService<AlorApiService>(),
                    _ => serviceProvider.GetRequiredService<TinkoffApiService>()
                };
            });


            

            // Регистрируем ViewModels и Windows
            services.AddTransient<MainViewModel>();
            services.AddSingleton<MainWindow>();
            services.AddTransient<StrategyViewModel>();
        }
    }
}