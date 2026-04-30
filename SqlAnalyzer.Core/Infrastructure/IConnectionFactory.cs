using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SqlAnalyzer.Core.Infrastructure
{
    public interface IConnectionFactory
    {
        /// <summary>
        /// Creates and opens a connection to the specified database (synchronous).
        /// </summary>
        SqlConnection CreateOpenConnection(string databaseName);

        /// <summary>
        /// Creates and opens a connection to the specified database (asynchronous).
        /// </summary>
        Task<SqlConnection> CreateOpenConnectionAsync(string databaseName, CancellationToken cancellationToken = default);
    }
}
