using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SqlAnalyzer.Core.Models;

namespace SqlAnalyzer.Core.Infrastructure
{
    /// <summary>
    /// Builds SqlConnections by swapping the Initial Catalog in the base connection string.
    /// One logical connection string is kept per catalog to allow pooling.
    /// </summary>
    public sealed class ConnectionFactory : IConnectionFactory
    {
        private readonly string _baseConnectionString;
        private readonly int _connectTimeout;

        // Cache built connection strings by database name (lower-case key).
        private readonly ConcurrentDictionary<string, string> _connectionStringCache
            = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Creates a factory from an <see cref="AnalysisOptions"/> that holds a base connection string.
        /// </summary>
        public ConnectionFactory(AnalysisOptions options)
        {
            if (options is null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.BaseConnectionString))
                throw new ArgumentException("BaseConnectionString must be provided.", nameof(options));

            _baseConnectionString = options.BaseConnectionString;
            _connectTimeout = options.ConnectionTimeoutSeconds;
        }

        /// <summary>
        /// Creates a factory from an existing <see cref="SqlConnection"/> provided by the caller.
        /// The server and authentication details are reused; only the Initial Catalog is swapped
        /// per-query so that multi-catalog resolution still works correctly.
        /// The original connection is NOT owned or closed by this factory.
        /// </summary>
        /// <param name="existingConnection">
        ///   An open or closed <see cref="SqlConnection"/> whose connection string is used as template.
        /// </param>
        /// <param name="connectTimeoutSeconds">Connection timeout for newly created connections (default: 30).</param>
        public ConnectionFactory(SqlConnection existingConnection, int connectTimeoutSeconds = 30)
        {
            if (existingConnection is null) throw new ArgumentNullException(nameof(existingConnection));

            // Extract the raw connection string.  If the connection is already open we can still
            // read its ConnectionString property — it always reflects the original string.
            string rawConnStr = existingConnection.ConnectionString;
            if (string.IsNullOrWhiteSpace(rawConnStr))
                throw new ArgumentException("The provided SqlConnection has an empty ConnectionString.",
                    nameof(existingConnection));

            // Strip the password from the builder only if it was already persisted in the string;
            // SqlConnectionStringBuilder never re-exposes a password that was supplied via
            // Integrated Security, so this is safe.
            _baseConnectionString = rawConnStr;
            _connectTimeout = connectTimeoutSeconds;
        }

        /// <inheritdoc />
        public SqlConnection CreateOpenConnection(string databaseName)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
                throw new ArgumentException("Database name must not be empty.", nameof(databaseName));

            string connStr = _connectionStringCache.GetOrAdd(databaseName, BuildConnectionString);
            var conn = new SqlConnection(connStr);
            conn.Open();
            return conn;
        }

        /// <inheritdoc />
        public async Task<SqlConnection> CreateOpenConnectionAsync(string databaseName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
                throw new ArgumentException("Database name must not be empty.", nameof(databaseName));

            string connStr = _connectionStringCache.GetOrAdd(databaseName, BuildConnectionString);
            var conn = new SqlConnection(connStr);
            await conn.OpenAsync(cancellationToken);
            return conn;
        }

        private string BuildConnectionString(string databaseName)
        {
            var builder = new SqlConnectionStringBuilder(_baseConnectionString)
            {
                InitialCatalog = databaseName,
                ConnectTimeout = _connectTimeout
            };
            return builder.ConnectionString;
        }
    }
}
