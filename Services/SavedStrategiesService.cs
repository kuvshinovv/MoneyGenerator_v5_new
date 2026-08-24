using Microsoft.Data.Sqlite;
using MoneyGenerator_v5.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;

namespace MoneyGenerator_v5.Services
{
    public class SavedStrategyInfo : INotifyPropertyChanged
    {
        public int Id { get; set; }
        public string StrategyType { get; set; }
        public string InstrumentUid { get; set; }
        public string InstrumentTicker { get; set; }
        public string InstrumentName { get; set; }
        public string Timeframe { get; set; }
        public string ParametersJson { get; set; }
        public bool IsAutoStart { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastUsed { get; set; }

        // ✅ Добавляем свойства для быстрого доступа к параметрам
        [JsonIgnore]
        public bool UseGlobalStopLoss { get; set; }

        [JsonIgnore]
        public decimal GlobalStopLossPercent { get; set; }

        [JsonIgnore]
        public bool UseGlobalTakeProfit { get; set; }

        [JsonIgnore]
        public decimal GlobalTakeProfitPercent { get; set; }

        [JsonIgnore]
        public decimal Capital { get; set; }

        [JsonIgnore]
        public int MaxConcurrentTrades { get; set; }

        [JsonIgnore]
        public int LotSize { get; set; }

        [JsonIgnore]
        public decimal MaxRiskPercent { get; set; }

        [JsonIgnore]
        public bool UseTrailingStop { get; set; }

        [JsonIgnore]
        public decimal TrailingStopPercent { get; set; }

        // ✅ Добавляем свойство для выделения
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    // ✅ Вызываем событие только при реальном изменении
                    OnPropertyChanged();
                }
            }
        }

        public string DisplayName => $"{StrategyType} - {InstrumentTicker} ({Timeframe})";

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public static class SavedStrategiesService
    {
        private static readonly object _lock = new object();
        private static readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        private static string GetDbPath()
        {
            return System.IO.Path.Combine(
                System.AppDomain.CurrentDomain.BaseDirectory,
                "market_dataMG5.db");
        }

        /// <summary>
        /// Сохранение стратегии в БД (с проверкой на дубликаты по параметрам)
        /// </summary>
        public static async Task<int> SaveStrategyAsync(
            string strategyType,
            string instrumentUid,
            string instrumentTicker,
            string instrumentName,
            string timeframe,
            string parametersJson,
            bool isAutoStart = false)
        {
            await _semaphore.WaitAsync();
            try
            {
                string dbPath = GetDbPath();
                using var connection = new SqliteConnection($"Data Source={dbPath}");
                await connection.OpenAsync();

                // Проверяем, существует ли уже такая стратегия (по ключевым полям)
                var checkCommand = connection.CreateCommand();
                checkCommand.CommandText = @"
            SELECT Id, ParametersJson FROM SavedStrategies 
            WHERE StrategyType = @strategyType 
              AND InstrumentUid = @instrumentUid 
              AND Timeframe = @timeframe";
                checkCommand.Parameters.AddWithValue("@strategyType", strategyType);
                checkCommand.Parameters.AddWithValue("@instrumentUid", instrumentUid);
                checkCommand.Parameters.AddWithValue("@timeframe", timeframe);

                using var reader = await checkCommand.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    var existingId = reader.GetInt32(0);
                    var existingParamsJson = reader.GetString(1);

                    // ✅ Сравниваем параметры
                    if (AreParametersEqual(existingParamsJson, parametersJson))
                    {
                        // Параметры совпадают - обновляем только IsAutoStart и LastUsed
                        Debug.WriteLine($"Стратегия {strategyType} для {instrumentTicker} уже существует с такими же параметрами. Обновляем метаданные.");

                        var updateCommand = connection.CreateCommand();
                        updateCommand.CommandText = @"
                    UPDATE SavedStrategies 
                    SET IsAutoStart = @isAutoStart,
                        LastUsed = CURRENT_TIMESTAMP,
                        InstrumentName = @instrumentName
                    WHERE Id = @id";
                        updateCommand.Parameters.AddWithValue("@isAutoStart", isAutoStart ? 1 : 0);
                        updateCommand.Parameters.AddWithValue("@instrumentName", instrumentName ?? "");
                        updateCommand.Parameters.AddWithValue("@id", existingId);

                        await updateCommand.ExecuteNonQueryAsync();
                        return existingId;
                    }
                    else
                    {
                        // Параметры отличаются - создаем новую запись
                        Debug.WriteLine($"Стратегия {strategyType} для {instrumentTicker} существует, но параметры отличаются. Создаем новую запись.");

                        var insertCommand = connection.CreateCommand();
                        insertCommand.CommandText = @"
                    INSERT INTO SavedStrategies 
                    (StrategyType, InstrumentUid, InstrumentTicker, InstrumentName, Timeframe, ParametersJson, IsAutoStart)
                    VALUES (@strategyType, @instrumentUid, @instrumentTicker, @instrumentName, @timeframe, @parametersJson, @isAutoStart);
                    SELECT last_insert_rowid();";
                        insertCommand.Parameters.AddWithValue("@strategyType", strategyType);
                        insertCommand.Parameters.AddWithValue("@instrumentUid", instrumentUid);
                        insertCommand.Parameters.AddWithValue("@instrumentTicker", instrumentTicker);
                        insertCommand.Parameters.AddWithValue("@instrumentName", instrumentName ?? "");
                        insertCommand.Parameters.AddWithValue("@timeframe", timeframe);
                        insertCommand.Parameters.AddWithValue("@parametersJson", parametersJson);
                        insertCommand.Parameters.AddWithValue("@isAutoStart", isAutoStart ? 1 : 0);

                        var newId = (long)await insertCommand.ExecuteScalarAsync();
                        return (int)newId;
                    }
                }
                else
                {
                    // Нет такой стратегии - создаем новую
                    Debug.WriteLine($"Стратегия {strategyType} для {instrumentTicker} не найдена. Создаем новую запись.");

                    var insertCommand = connection.CreateCommand();
                    insertCommand.CommandText = @"
                INSERT INTO SavedStrategies 
                (StrategyType, InstrumentUid, InstrumentTicker, InstrumentName, Timeframe, ParametersJson, IsAutoStart)
                VALUES (@strategyType, @instrumentUid, @instrumentTicker, @instrumentName, @timeframe, @parametersJson, @isAutoStart);
                SELECT last_insert_rowid();";
                    insertCommand.Parameters.AddWithValue("@strategyType", strategyType);
                    insertCommand.Parameters.AddWithValue("@instrumentUid", instrumentUid);
                    insertCommand.Parameters.AddWithValue("@instrumentTicker", instrumentTicker);
                    insertCommand.Parameters.AddWithValue("@instrumentName", instrumentName ?? "");
                    insertCommand.Parameters.AddWithValue("@timeframe", timeframe);
                    insertCommand.Parameters.AddWithValue("@parametersJson", parametersJson);
                    insertCommand.Parameters.AddWithValue("@isAutoStart", isAutoStart ? 1 : 0);

                    var newId = (long)await insertCommand.ExecuteScalarAsync();
                    return (int)newId;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка сохранения стратегии: {ex.Message}");
                return -1;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Сравнение двух JSON строк с параметрами
        /// </summary>
        private static bool AreParametersEqual(string params1, string params2)
        {
            if (string.IsNullOrEmpty(params1) && string.IsNullOrEmpty(params2))
                return true;
            if (string.IsNullOrEmpty(params1) || string.IsNullOrEmpty(params2))
                return false;

            try
            {
                using var doc1 = JsonDocument.Parse(params1);
                using var doc2 = JsonDocument.Parse(params2);

                return JsonElement.DeepEquals(doc1.RootElement, doc2.RootElement);
            }
            catch
            {
                // Если не удалось распарсить, сравниваем как строки
                return params1 == params2;
            }
        }

        /// <summary>
        /// Получение всех сохраненных стратегий
        /// </summary>
        public static async Task<List<SavedStrategyInfo>> GetAllStrategiesAsync()
        {
            var result = new List<SavedStrategyInfo>();

            await _semaphore.WaitAsync();
            try
            {
                string dbPath = GetDbPath();
                using var connection = new SqliteConnection($"Data Source={dbPath}");
                await connection.OpenAsync();

                var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT Id, StrategyType, InstrumentUid, InstrumentTicker, InstrumentName, 
                           Timeframe, ParametersJson, IsAutoStart, CreatedAt, LastUsed
                    FROM SavedStrategies
                    ORDER BY StrategyType, InstrumentTicker";

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    result.Add(new SavedStrategyInfo
                    {
                        Id = reader.GetInt32(0),
                        StrategyType = reader.GetString(1),
                        InstrumentUid = reader.GetString(2),
                        InstrumentTicker = reader.GetString(3),
                        InstrumentName = reader.IsDBNull(4) ? null : reader.GetString(4),
                        Timeframe = reader.GetString(5),
                        ParametersJson = reader.GetString(6),
                        IsAutoStart = reader.GetInt32(7) == 1,
                        CreatedAt = DateTime.Parse(reader.GetString(8)),
                        LastUsed = DateTime.Parse(reader.GetString(9))
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка загрузки стратегий: {ex.Message}");
            }
            finally
            {
                _semaphore.Release();
            }

            return result;
        }

        /// <summary>
        /// Удаление сохраненной стратегии
        /// </summary>
        public static async Task<bool> DeleteStrategyAsync(int id)
        {
            await _semaphore.WaitAsync();
            try
            {
                string dbPath = GetDbPath();
                using var connection = new SqliteConnection($"Data Source={dbPath}");
                await connection.OpenAsync();

                var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM SavedStrategies WHERE Id = @id";
                command.Parameters.AddWithValue("@id", id);

                var rows = await command.ExecuteNonQueryAsync();
                return rows > 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка удаления стратегии: {ex.Message}");
                return false;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Удаление всех стратегий
        /// </summary>
        public static async Task<int> DeleteAllStrategiesAsync()
        {
            await _semaphore.WaitAsync();
            try
            {
                string dbPath = GetDbPath();
                using var connection = new SqliteConnection($"Data Source={dbPath}");
                await connection.OpenAsync();

                var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM SavedStrategies";

                return await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка удаления всех стратегий: {ex.Message}");
                return 0;
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }
}