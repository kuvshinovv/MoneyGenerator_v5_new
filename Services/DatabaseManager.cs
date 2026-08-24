using Microsoft.Data.Sqlite;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MoneyGenerator_v5.Services
{
    public static class DatabaseManager
    {
        private static readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private static readonly string _dbPath = System.IO.Path.Combine(
            System.AppDomain.CurrentDomain.BaseDirectory,
            "market_dataMG5.db");

        private static SqliteConnection _sharedConnection;
        private static int _connectionCount = 0;

        public static async Task<SqliteConnection> GetConnectionAsync()
        {
            await _lock.WaitAsync();
            try
            {
                if (_sharedConnection == null)
                {
                    _sharedConnection = new SqliteConnection($"Data Source={_dbPath}");
                    await _sharedConnection.OpenAsync();
                    Debug.WriteLine("DatabaseManager: Создано новое соединение");
                }
                _connectionCount++;
                Debug.WriteLine($"DatabaseManager: Выдано соединение (активных: {_connectionCount})");
                return _sharedConnection;
            }
            finally
            {
                _lock.Release();
            }
        }

        public static void ReleaseConnection()
        {
            _lock.Wait();
            try
            {
                if (_connectionCount > 0)
                {
                    _connectionCount--;
                    Debug.WriteLine($"DatabaseManager: Возвращено соединение (активных: {_connectionCount})");
                }

                if (_connectionCount == 0 && _sharedConnection != null)
                {
                    // Не закрываем соединение, просто оставляем открытым
                    Debug.WriteLine("DatabaseManager: Соединение остается открытым");
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        public static async Task ExecuteWithLockAsync(Func<Task> operation, string operationName = "")
        {
            await _lock.WaitAsync();
            try
            {
                await operation();
            }
            finally
            {
                _lock.Release();
            }
        }

        public static async Task<T> ExecuteWithLockAsync<T>(Func<Task<T>> operation, string operationName = "")
        {
            await _lock.WaitAsync();
            try
            {
                return await operation();
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}