namespace SqlAnalyzer.Core.Models
{
    /// <summary>
    /// Configuration options for the SQL analysis pipeline.
    /// </summary>
    public sealed class AnalysisOptions
    {
        /// <summary>
        /// Default database/catalog to use when no USE statement or explicit qualifier is present.
        /// Required.
        /// </summary>
        public string DefaultDatabase { get; set; } = string.Empty;

        /// <summary>
        /// Base connection string. The Initial Catalog will be swapped dynamically per-database.
        /// Required when constructing <see cref="SqlAnalyzer.Core.Orchestration.SqlAnalysisOrchestrator"/>
        /// via <c>new SqlAnalysisOrchestrator(options)</c>.
        /// Leave null/empty when passing an existing <see cref="Microsoft.Data.SqlClient.SqlConnection"/>
        /// directly to the orchestrator constructor.
        /// </summary>
        public string? BaseConnectionString { get; set; }

        /// <summary>
        /// Maximum recursion depth for dependency resolution. Default: 5.
        /// </summary>
        public int MaxDependencyDepth { get; set; } = 5;

        /// <summary>
        /// Whether to resolve dependencies recursively (Level 3).
        /// Default: true.
        /// </summary>
        public bool ResolveRecursiveDependencies { get; set; } = true;

        /// <summary>
        /// Whether to resolve and document Tables found in references (they have no SQL definition).
        /// Default: false — tables are identified but not retrieved.
        /// </summary>
        public bool IncludeTables { get; set; } = false;

        /// <summary>
        /// Connection timeout in seconds. Default: 30.
        /// </summary>
        public int ConnectionTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Command timeout in seconds. Default: 60.
        /// </summary>
        public int CommandTimeoutSeconds { get; set; } = 60;
    }
}
