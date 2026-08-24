using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace MoneyGenerator_v5.Services
{
    public class TokenManager
    {
        private readonly ILogger<TokenManager> _logger;
        private const string SecretFileName = "tokens.secret";

        public TokenManager(ILogger<TokenManager> logger)
        {
            _logger = logger;
        }

        // Загружает все токены для всех провайдеров
        public ProviderTokens LoadAllTokens()
        {
            try
            {
                if (!File.Exists(SecretFileName))
                {
                    _logger.LogWarning("Файл с токенами не найден. Создаю файл {FileName}", SecretFileName);
                    CreateDefaultTokenFile();
                    return new ProviderTokens();
                }

                var json = File.ReadAllText(SecretFileName);
                var tokens = JsonSerializer.Deserialize<ProviderTokens>(json);

                if (tokens == null)
                {
                    _logger.LogError("Не удалось десериализовать файл токенов");
                    return new ProviderTokens();
                }

                // Инициализируем свойства, если они null
                tokens.Tinkoff ??= new ProviderToken();
                tokens.Finam ??= new ProviderToken();
                tokens.Alor ??= new ProviderToken();

                // Для отладки
                Debug.WriteLine($"DEBUG: Загружены токены:");
                Debug.WriteLine($"  Tinkoff: Sandbox={(string.IsNullOrEmpty(tokens.Tinkoff.SandboxToken) ? "empty" : "***")}, Real={(string.IsNullOrEmpty(tokens.Tinkoff.RealToken) ? "empty" : "***")}");
                Debug.WriteLine($"  Finam: Sandbox={(string.IsNullOrEmpty(tokens.Finam.SandboxToken) ? "empty" : "***")}, Real={(string.IsNullOrEmpty(tokens.Finam.RealToken) ? "empty" : "***")}");
                Debug.WriteLine($"  Alor: Sandbox={(string.IsNullOrEmpty(tokens.Alor.SandboxToken) ? "empty" : "***")}, Real={(string.IsNullOrEmpty(tokens.Alor.RealToken) ? "empty" : "***")}");

                _logger.LogInformation("Токены загружены из файла {FileName}", SecretFileName);
                return tokens;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка загрузки токенов из файла {FileName}", SecretFileName);
                return new ProviderTokens();
            }
        }

        // Загружает токены для конкретного провайдера
        public (string SandboxToken, string RealToken) LoadProviderTokens(string providerName)
        {
            var allTokens = LoadAllTokens();

            return providerName switch
            {
                "Тинькофф" => (allTokens.Tinkoff.SandboxToken, allTokens.Tinkoff.RealToken),
                "Финам" => (allTokens.Finam.SandboxToken, allTokens.Finam.RealToken),
                "Алор" => (allTokens.Alor.SandboxToken, allTokens.Alor.RealToken),
                _ => ("", "")
            };
        }

        // Сохраняет токены для конкретного провайдера
        public void SaveProviderTokens(string providerName, string sandboxToken, string realToken)
        {
            try
            {
                // Загружаем текущие токены
                var allTokens = LoadAllTokens();

                // Обновляем токены для указанного провайдера
                var providerToken = new ProviderToken
                {
                    SandboxToken = sandboxToken?.Trim() ?? "",
                    RealToken = realToken?.Trim() ?? ""
                };

                switch (providerName)
                {
                    case "Тинькофф":
                        allTokens.Tinkoff = providerToken;
                        break;
                    case "Финам":
                        allTokens.Finam = providerToken;
                        break;
                    case "Алор":
                        allTokens.Alor = providerToken;
                        break;
                    default:
                        throw new ArgumentException($"Неизвестный провайдер: {providerName}");
                }

                // Сохраняем обратно в файл
                SaveAllTokens(allTokens);

                _logger.LogInformation("Токены для провайдера {Provider} сохранены", providerName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка сохранения токенов для провайдера {Provider}", providerName);
            }
        }

        // Сохраняет все токены сразу
        public void SaveAllTokens(ProviderTokens tokens)
        {
            try
            {
                // Проверяем и инициализируем все свойства
                if (tokens == null)
                {
                    tokens = new ProviderTokens();
                }

                tokens.Tinkoff ??= new ProviderToken();
                tokens.Finam ??= new ProviderToken();
                tokens.Alor ??= new ProviderToken();

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };

                var json = JsonSerializer.Serialize(tokens, options);
                File.WriteAllText(SecretFileName, json);

                _logger.LogInformation("Все токены сохранены в файл {FileName}", SecretFileName);

                // Для отладки
                Debug.WriteLine($"DEBUG: Сохраненные токены:");
                Debug.WriteLine($"  Tinkoff: Sandbox={(string.IsNullOrEmpty(tokens.Tinkoff.SandboxToken) ? "empty" : "***")}, Real={(string.IsNullOrEmpty(tokens.Tinkoff.RealToken) ? "empty" : "***")}");
                Debug.WriteLine($"  Finam: Sandbox={(string.IsNullOrEmpty(tokens.Finam.SandboxToken) ? "empty" : "***")}, Real={(string.IsNullOrEmpty(tokens.Finam.RealToken) ? "empty" : "***")}");
                Debug.WriteLine($"  Alor: Sandbox={(string.IsNullOrEmpty(tokens.Alor.SandboxToken) ? "empty" : "***")}, Real={(string.IsNullOrEmpty(tokens.Alor.RealToken) ? "empty" : "***")}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка сохранения всех токенов");
                Debug.WriteLine($"ERROR при сохранении токенов: {ex.Message}");
            }
        }

        private void CreateDefaultTokenFile()
        {
            try
            {
                var defaultTokens = new ProviderTokens();

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };

                var json = JsonSerializer.Serialize(defaultTokens, options);
                File.WriteAllText(SecretFileName, json);

                _logger.LogInformation("Создан файл с токенами по умолчанию: {FileName}", SecretFileName);
                _logger.LogWarning("Пожалуйста, добавьте ваши API токены в файл {FileName}", SecretFileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка создания файла токенов по умолчанию");
            }
        }

        // Классы для хранения токенов с конструкторами
        public class ProviderTokens
        {
            public ProviderToken Tinkoff { get; set; }
            public ProviderToken Finam { get; set; }
            public ProviderToken Alor { get; set; }

            public ProviderTokens()
            {
                Tinkoff = new ProviderToken();
                Finam = new ProviderToken();
                Alor = new ProviderToken();
            }
        }

        public class ProviderToken
        {
            public string SandboxToken { get; set; } = "";
            public string RealToken { get; set; } = "";
        }
    }
}