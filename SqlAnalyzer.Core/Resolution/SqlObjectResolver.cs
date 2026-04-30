using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using SqlAnalyzer.Core.Infrastructure;
using SqlAnalyzer.Core.Models;

namespace SqlAnalyzer.Core.Resolution
{
    /// <summary>
    /// Resolves SQL object references against SQL Server metadata using Dapper.
    /// Uses the correct catalog for every query — never cross-database guessing.
    /// </summary>
    public sealed class SqlObjectResolver
    {
        private readonly IConnectionFactory _connectionFactory;
        private readonly int _commandTimeout;

        public SqlObjectResolver(IConnectionFactory connectionFactory, int commandTimeoutSeconds = 60)
        {
            _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _commandTimeout = commandTimeoutSeconds;
        }

        // ------------------------------------------------------------------ public sync API

        /// <summary>
        /// Resolves the type and definition for a single object reference.
        /// Returns null when the object genuinely cannot be found in SQL Server metadata.
        /// </summary>
        public ResolvedSqlObject? Resolve(SqlObjectReference reference, List<Diagnostic> diagnostics)
        {
            if (reference is null) throw new ArgumentNullException(nameof(reference));
            diagnostics ??= new List<Diagnostic>();

            string database = reference.EffectiveDatabase;
            if (string.IsNullOrWhiteSpace(database))
            {
                diagnostics.Add(Diagnostic.Warning($"No database context for {reference.FullName} — skipped."));
                return null;
            }

            if (!string.IsNullOrWhiteSpace(reference.Server))
            {
                diagnostics.Add(Diagnostic.Info($"Linked-server reference {reference.FullName} cannot be resolved locally — skipped."));
                return null;
            }

            try
            {
                using (SqlConnection conn = _connectionFactory.CreateOpenConnection(database))
                {
                    var meta = QueryObjectMetadata(conn, reference);
                    if (meta == null) return null;

                    string? rawDef = null;
                    bool defIsNull = false;
                    bool isEncrypted = false;

                    if (IsDefinable(meta.TypeCode))
                        (rawDef, defIsNull, isEncrypted) = QueryDefinition(conn, meta.ObjectId, reference, diagnostics);

                    return new ResolvedSqlObject
                    {
                        Reference        = reference,
                        ObjectId         = meta.ObjectId,
                        ObjectType       = MapTypeCode(meta.TypeCode),
                        SqlTypeCode      = meta.TypeCode?.Trim(),
                        RawDefinition    = rawDef,
                        DefinitionIsNull = defIsNull,
                        IsEncrypted      = isEncrypted
                    };
                }
            }
            catch (SqlException ex) when (ex.Number == 4060 || ex.Number == 18456)
            {
                diagnostics.Add(Diagnostic.Error($"Cannot connect to database '{database}': {ex.Message}"));
                return null;
            }
            catch (Exception ex)
            {
                diagnostics.Add(Diagnostic.Error($"Resolving {reference.FullName}: {ex.Message}"));
                return null;
            }
        }

        /// <summary>
        /// Resolves all direct SQL dependencies of a given object.
        /// </summary>
        public List<SqlObjectReference> ResolveDependencies(ResolvedSqlObject obj, List<Diagnostic> diagnostics)
        {
            if (obj is null) throw new ArgumentNullException(nameof(obj));
            diagnostics ??= new List<Diagnostic>();

            string database = obj.Reference.EffectiveDatabase;
            if (string.IsNullOrWhiteSpace(database)) return new List<SqlObjectReference>();

            try
            {
                using (SqlConnection conn = _connectionFactory.CreateOpenConnection(database))
                {
                    // ObjectId was stored during Resolve — no additional GetObjectId round-trip needed.
                    return QueryDependencies(conn, obj.ObjectId, database, diagnostics);
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add(Diagnostic.Error($"Querying dependencies of {obj.Reference.FullName}: {ex.Message}"));
                return new List<SqlObjectReference>();
            }
        }

        // ------------------------------------------------------------------ public async API

        public async Task<ResolvedSqlObject?> ResolveAsync(
            SqlObjectReference reference,
            List<Diagnostic> diagnostics,
            CancellationToken cancellationToken = default)
        {
            if (reference is null) throw new ArgumentNullException(nameof(reference));
            diagnostics ??= new List<Diagnostic>();

            string database = reference.EffectiveDatabase;
            if (string.IsNullOrWhiteSpace(database))
            {
                diagnostics.Add(Diagnostic.Warning($"No database context for {reference.FullName} — skipped."));
                return null;
            }

            if (!string.IsNullOrWhiteSpace(reference.Server))
            {
                diagnostics.Add(Diagnostic.Info($"Linked-server reference {reference.FullName} cannot be resolved locally — skipped."));
                return null;
            }

            try
            {
                using (SqlConnection conn = await _connectionFactory.CreateOpenConnectionAsync(database, cancellationToken))
                {
                    var meta = await QueryObjectMetadataAsync(conn, reference, cancellationToken);
                    if (meta == null) return null;

                    string? rawDef = null;
                    bool defIsNull = false;
                    bool isEncrypted = false;

                    if (IsDefinable(meta.TypeCode))
                        (rawDef, defIsNull, isEncrypted) = await QueryDefinitionAsync(conn, meta.ObjectId, reference, diagnostics, cancellationToken);

                    return new ResolvedSqlObject
                    {
                        Reference        = reference,
                        ObjectId         = meta.ObjectId,
                        ObjectType       = MapTypeCode(meta.TypeCode),
                        SqlTypeCode      = meta.TypeCode?.Trim(),
                        RawDefinition    = rawDef,
                        DefinitionIsNull = defIsNull,
                        IsEncrypted      = isEncrypted
                    };
                }
            }
            catch (SqlException ex) when (ex.Number == 4060 || ex.Number == 18456)
            {
                diagnostics.Add(Diagnostic.Error($"Cannot connect to database '{database}': {ex.Message}"));
                return null;
            }
            catch (Exception ex)
            {
                diagnostics.Add(Diagnostic.Error($"Resolving {reference.FullName}: {ex.Message}"));
                return null;
            }
        }

        public async Task<List<SqlObjectReference>> ResolveDependenciesAsync(
            ResolvedSqlObject obj,
            List<Diagnostic> diagnostics,
            CancellationToken cancellationToken = default)
        {
            if (obj is null) throw new ArgumentNullException(nameof(obj));
            diagnostics ??= new List<Diagnostic>();

            string database = obj.Reference.EffectiveDatabase;
            if (string.IsNullOrWhiteSpace(database)) return new List<SqlObjectReference>();

            try
            {
                using (SqlConnection conn = await _connectionFactory.CreateOpenConnectionAsync(database, cancellationToken))
                {
                    return await QueryDependenciesAsync(conn, obj.ObjectId, database, diagnostics, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add(Diagnostic.Error($"Querying dependencies of {obj.Reference.FullName}: {ex.Message}"));
                return new List<SqlObjectReference>();
            }
        }

        // ------------------------------------------------------------------ sync metadata queries

        private ObjectMetaRow? QueryObjectMetadata(SqlConnection conn, SqlObjectReference reference)
        {
            string schema = string.IsNullOrWhiteSpace(reference.Schema) ? "dbo" : reference.Schema;
            string name   = reference.ObjectName;

            const string sql = @"
SELECT
    o.object_id   AS ObjectId,
    o.type        AS TypeCode,
    o.name        AS ObjectName,
    s.name        AS SchemaName,
    CASE
        WHEN m.uses_native_compilation = 1 THEN 1
        WHEN m.definition IS NULL
             AND o.type IN ('P','V','FN','TF','IF','TR','RF')
             AND OBJECTPROPERTY(o.object_id, 'IsEncrypted') = 1 THEN 1
        ELSE 0
    END           AS IsEncrypted
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id
WHERE s.name      = @schema
  AND o.name      = @name
  AND o.type NOT IN ('SQ','IT','S');";

            return conn.QueryFirstOrDefault<ObjectMetaRow>(sql,
                new { schema, name },
                commandTimeout: _commandTimeout);
        }

        private (string? definition, bool isNull, bool isEncrypted) QueryDefinition(
            SqlConnection conn, int objectId, SqlObjectReference reference, List<Diagnostic> diagnostics)
        {
            const string sql = @"
SELECT
    m.definition,
    CASE WHEN m.definition IS NULL THEN 1 ELSE 0 END AS DefinitionIsNull,
    CASE
        WHEN m.definition IS NULL
             AND OBJECTPROPERTY(m.object_id,'IsEncrypted') = 1 THEN 1
        ELSE 0
    END AS IsEncrypted
FROM sys.sql_modules m
WHERE m.object_id = @objectId;";

            var row = conn.QueryFirstOrDefault<DefinitionRow>(sql,
                new { objectId },
                commandTimeout: _commandTimeout);

            if (row == null)
            {
                diagnostics.Add(Diagnostic.Warning($"No sys.sql_modules row found for object_id={objectId} ({reference.FullName})."));
                return (null, true, false);
            }

            return (row.Definition, row.DefinitionIsNull, row.IsEncrypted);
        }

        private List<SqlObjectReference> QueryDependencies(
            SqlConnection conn, int objectId, string ownerDatabase, List<Diagnostic> diagnostics)
        {
            const string sql = @"
SELECT
    ISNULL(d.referenced_server_name, '')   AS Server,
    ISNULL(d.referenced_database_name, '') AS Database,
    ISNULL(d.referenced_schema_name,  '')  AS SchemaName,
    d.referenced_entity_name               AS ObjectName,
    o.type                                 AS TypeCode
FROM sys.sql_expression_dependencies d
LEFT JOIN sys.objects o
    ON o.object_id = d.referenced_id
WHERE d.referencing_id = @objectId
  AND d.referenced_class = 1
  AND (o.type IS NULL OR o.type NOT IN ('SQ','IT','S'));";

            var rows = conn.Query<DependencyRow>(sql,
                new { objectId },
                commandTimeout: _commandTimeout);

            return BuildReferences(rows, ownerDatabase);
        }

        // ------------------------------------------------------------------ async metadata queries

        private async Task<ObjectMetaRow?> QueryObjectMetadataAsync(
            SqlConnection conn, SqlObjectReference reference, CancellationToken ct)
        {
            string schema = string.IsNullOrWhiteSpace(reference.Schema) ? "dbo" : reference.Schema;
            string name   = reference.ObjectName;

            const string sql = @"
SELECT
    o.object_id   AS ObjectId,
    o.type        AS TypeCode,
    o.name        AS ObjectName,
    s.name        AS SchemaName,
    CASE
        WHEN m.uses_native_compilation = 1 THEN 1
        WHEN m.definition IS NULL
             AND o.type IN ('P','V','FN','TF','IF','TR','RF')
             AND OBJECTPROPERTY(o.object_id, 'IsEncrypted') = 1 THEN 1
        ELSE 0
    END           AS IsEncrypted
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id
WHERE s.name      = @schema
  AND o.name      = @name
  AND o.type NOT IN ('SQ','IT','S');";

            return await conn.QueryFirstOrDefaultAsync<ObjectMetaRow>(
                new CommandDefinition(sql, new { schema, name }, commandTimeout: _commandTimeout, cancellationToken: ct));
        }

        private async Task<(string? definition, bool isNull, bool isEncrypted)> QueryDefinitionAsync(
            SqlConnection conn, int objectId, SqlObjectReference reference,
            List<Diagnostic> diagnostics, CancellationToken ct)
        {
            const string sql = @"
SELECT
    m.definition,
    CASE WHEN m.definition IS NULL THEN 1 ELSE 0 END AS DefinitionIsNull,
    CASE
        WHEN m.definition IS NULL
             AND OBJECTPROPERTY(m.object_id,'IsEncrypted') = 1 THEN 1
        ELSE 0
    END AS IsEncrypted
FROM sys.sql_modules m
WHERE m.object_id = @objectId;";

            var row = await conn.QueryFirstOrDefaultAsync<DefinitionRow>(
                new CommandDefinition(sql, new { objectId }, commandTimeout: _commandTimeout, cancellationToken: ct));

            if (row == null)
            {
                diagnostics.Add(Diagnostic.Warning($"No sys.sql_modules row found for object_id={objectId} ({reference.FullName})."));
                return (null, true, false);
            }

            return (row.Definition, row.DefinitionIsNull, row.IsEncrypted);
        }

        private async Task<List<SqlObjectReference>> QueryDependenciesAsync(
            SqlConnection conn, int objectId, string ownerDatabase,
            List<Diagnostic> diagnostics, CancellationToken ct)
        {
            const string sql = @"
SELECT
    ISNULL(d.referenced_server_name, '')   AS Server,
    ISNULL(d.referenced_database_name, '') AS Database,
    ISNULL(d.referenced_schema_name,  '')  AS SchemaName,
    d.referenced_entity_name               AS ObjectName,
    o.type                                 AS TypeCode
FROM sys.sql_expression_dependencies d
LEFT JOIN sys.objects o
    ON o.object_id = d.referenced_id
WHERE d.referencing_id = @objectId
  AND d.referenced_class = 1
  AND (o.type IS NULL OR o.type NOT IN ('SQ','IT','S'));";

            var rows = await conn.QueryAsync<DependencyRow>(
                new CommandDefinition(sql, new { objectId }, commandTimeout: _commandTimeout, cancellationToken: ct));

            return BuildReferences(rows, ownerDatabase);
        }

        // ------------------------------------------------------------------ shared helpers

        private static List<SqlObjectReference> BuildReferences(IEnumerable<DependencyRow> rows, string ownerDatabase)
        {
            var result = new List<SqlObjectReference>();
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.ObjectName)) continue;

                string depDatabase = !string.IsNullOrWhiteSpace(row.Database) ? row.Database : ownerDatabase;

                result.Add(new SqlObjectReference
                {
                    Server     = string.IsNullOrWhiteSpace(row.Server)     ? null : row.Server,
                    Database   = string.IsNullOrWhiteSpace(row.Database)   ? null : row.Database,
                    Schema     = string.IsNullOrWhiteSpace(row.SchemaName) ? null : row.SchemaName,
                    ObjectName = row.ObjectName,
                    ActiveDatabaseContext = depDatabase
                });
            }
            return result;
        }

        // ------------------------------------------------------------------ type helpers

        private static bool IsDefinable(string? typeCode)
        {
            if (typeCode is null) return false;
            string t = typeCode.Trim().ToUpperInvariant();
            return t == "V" || t == "P" || t == "FN" || t == "TF" || t == "IF"
                || t == "TR" || t == "RF" || t == "PC";
        }

        private static SqlObjectType MapTypeCode(string? typeCode)
        {
            if (typeCode is null) return SqlObjectType.Unknown;
            return typeCode.Trim().ToUpperInvariant() switch
            {
                "V"  => SqlObjectType.View,
                "P"  => SqlObjectType.StoredProcedure,
                "PC" => SqlObjectType.StoredProcedure,
                "FN" => SqlObjectType.ScalarFunction,
                "TF" => SqlObjectType.TableValuedFunction,
                "IF" => SqlObjectType.InlineTableValuedFunction,
                "U"  => SqlObjectType.Table,
                "SN" => SqlObjectType.Synonym,
                "TR" => SqlObjectType.Trigger,
                _    => SqlObjectType.Unknown
            };
        }

        // ------------------------------------------------------------------ private DTO rows (Dapper)

        private sealed class ObjectMetaRow
        {
            public int ObjectId { get; set; }
            public string? TypeCode { get; set; }
            public string? ObjectName { get; set; }
            public string? SchemaName { get; set; }
            public bool IsEncrypted { get; set; }
        }

        private sealed class DefinitionRow
        {
            public string? Definition { get; set; }
            public bool DefinitionIsNull { get; set; }
            public bool IsEncrypted { get; set; }
        }

        private sealed class DependencyRow
        {
            public string Server { get; set; } = string.Empty;
            public string Database { get; set; } = string.Empty;
            public string SchemaName { get; set; } = string.Empty;
            public string ObjectName { get; set; } = string.Empty;
            public string? TypeCode { get; set; }
        }
    }
}

