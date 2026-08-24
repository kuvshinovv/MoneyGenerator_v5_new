using Microsoft.Data.Sqlite;
using MoneyGenerator_v5.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace MoneyGenerator_v5.Services
{
    public static class EquityService
    {
        // Событие для уведомления о новых записях
        public static event Action<string, string> OnEquityDataUpdated; // provider, accountId


        /// <summary>
        /// Создание таблицы эквити если она не существует
        /// </summary>
        public static async Task EnsureTableExistsAsync()
        {
            try
            {
                string dbPath = System.IO.Path.Combine(
                System.AppDomain.CurrentDomain.BaseDirectory,
                "market_dataMG5.db");

                using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
                await connection.OpenAsync();

                var command = connection.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS EquityJournal (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Provider TEXT NOT NULL,
                        AccountId TEXT NOT NULL,
                        AccountType TEXT NOT NULL,
                        Balance REAL NOT NULL,
                        RecordTime TEXT NOT NULL,
                        Currency TEXT DEFAULT 'RUB'
                    );
                    
                    CREATE INDEX IF NOT EXISTS idx_equity_account_time 
                    ON EquityJournal(AccountId, RecordTime DESC);
                ";

                await command.ExecuteNonQueryAsync();
                Debug.WriteLine("Таблица EquityJournal создана/проверена");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка создания таблицы EquityJournal: {ex.Message}");
            }
        }

        /// <summary>
        /// Запись баланса в таблицу эквити
        /// </summary>
        public static async Task SaveRecordAsync(string provider, string accountId, string accountType, decimal balance, string currency = "RUB")
        {
            try
            {
                if (balance > 0)
                {
                    string dbPath = System.IO.Path.Combine(
               System.AppDomain.CurrentDomain.BaseDirectory,
               "market_dataMG5.db");

                    using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
                    await connection.OpenAsync();

                    var command = connection.CreateCommand();
                    command.CommandText = @"
                    INSERT INTO EquityJournal (Provider, AccountId, AccountType, Balance, RecordTime, Currency)
                    VALUES (@provider, @accountId, @accountType, @balance, @recordTime, @currency)
                ";

                    command.Parameters.AddWithValue("@provider", provider);
                    command.Parameters.AddWithValue("@accountId", accountId);
                    command.Parameters.AddWithValue("@accountType", accountType);
                    command.Parameters.AddWithValue("@balance", balance);
                    command.Parameters.AddWithValue("@recordTime", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    command.Parameters.AddWithValue("@currency", currency);

                    await command.ExecuteNonQueryAsync();
                    //Debug.WriteLine($"Сохранен баланс: {provider}/{accountType} - {balance:F2} {currency}");

                    // ✅ ВЫЗЫВАЕМ СОБЫТИЕ ПОСЛЕ СОХРАНЕНИЯ
                    OnEquityDataUpdated?.Invoke(provider, accountId);
                }
                
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка сохранения баланса: {ex.Message}");
            }
        }

        /// <summary>
        /// Получение истории эквити для графика
        /// </summary>
        public static async Task<List<EquityRecord>> GetHistoryAsync(string provider, string accountId, int days = 30)
        {
            var records = new List<EquityRecord>();

            try
            {
                string dbPath = System.IO.Path.Combine(
               System.AppDomain.CurrentDomain.BaseDirectory,
               "market_dataMG5.db");

                using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
                await connection.OpenAsync();

                var command = connection.CreateCommand();
                if (days > 0)
                {
                    command.CommandText = @"
                        SELECT Id, Provider, AccountId, AccountType, Balance, RecordTime, Currency
                        FROM EquityJournal
                        WHERE Provider = @provider AND AccountId = @accountId
                        AND RecordTime >= datetime('now', '-' || @days || ' days')
                        ORDER BY RecordTime ASC
                    ";
                    command.Parameters.AddWithValue("@days", days);
                }
                else
                {
                    command.CommandText = @"
                        SELECT Id, Provider, AccountId, AccountType, Balance, RecordTime, Currency
                        FROM EquityJournal
                        WHERE Provider = @provider AND AccountId = @accountId
                        ORDER BY RecordTime ASC
                    ";
                }

                command.Parameters.AddWithValue("@provider", provider);
                command.Parameters.AddWithValue("@accountId", accountId);

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    records.Add(new EquityRecord
                    {
                        Id = reader.GetInt32(0),
                        Provider = reader.GetString(1),
                        AccountId = reader.GetString(2),
                        AccountType = reader.GetString(3),
                        Balance = reader.GetDecimal(4),
                        RecordTime = DateTime.Parse(reader.GetString(5)),
                        Currency = reader.GetString(6)
                    });
                }

                //Debug.WriteLine($"Загружено {records.Count} записей эквити для {provider}/{accountId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка загрузки эквити: {ex.Message}");
            }

            return records;
        }

        /// <summary>
        /// Получение списка всех провайдеров, для которых есть данные
        /// </summary>
        public static async Task<List<string>> GetProvidersWithDataAsync()
        {
            var providers = new List<string>();

            try
            {
                string dbPath = System.IO.Path.Combine(
                    System.AppDomain.CurrentDomain.BaseDirectory,
                    "market_dataMG5.db");

                using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
                await connection.OpenAsync();

                var command = connection.CreateCommand();
                command.CommandText = @"
                SELECT DISTINCT Provider
                FROM EquityJournal
                ORDER BY Provider
            ";

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    providers.Add(reader.GetString(0));
                }

                Debug.WriteLine($"Найдено провайдеров с данными: {providers.Count}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка получения списка провайдеров: {ex.Message}");
            }

            return providers;
        }
    }
}