namespace SqlAnalyzer.Core.Models
{
    /// <summary>
    /// A SQL object that has been resolved against SQL Server metadata:
    /// its type is confirmed, its definition is retrieved and formatted.
    /// </summary>
    public sealed class ResolvedSqlObject
    {
        /// <summary>Original reference as extracted from the script.</summary>
        public SqlObjectReference Reference { get; set; } = null!;

        /// <summary>SQL Server confirmed object type.</summary>
        public SqlObjectType ObjectType { get; set; }

        /// <summary>
        /// SQL Server object_id from sys.objects. Stored to avoid a redundant GetObjectId
        /// call when querying sys.sql_expression_dependencies.
        /// </summary>
        public int ObjectId { get; set; }

        /// <summary>SQL Server internal type code (V, P, FN, TF, IF …).</summary>
        public string? SqlTypeCode { get; set; }

        /// <summary>Raw definition from sys.sql_modules.definition.</summary>
        public string? RawDefinition { get; set; }

        /// <summary>Formatted / pretty-printed T-SQL definition.</summary>
        public string? FormattedDefinition { get; set; }

        /// <summary>Whether the definition was NULL or empty in sys.sql_modules.</summary>
        public bool DefinitionIsNull { get; set; }

        /// <summary>Whether this object is encrypted (definition unavailable).</summary>
        public bool IsEncrypted { get; set; }

        /// <summary>Depth at which this object was discovered (0 = directly referenced in original script).</summary>
        public int DiscoveryDepth { get; set; }

        /// <summary>Resolution warnings or notes (e.g. fallback used).</summary>
        public string? Notes { get; set; }
    }
}
