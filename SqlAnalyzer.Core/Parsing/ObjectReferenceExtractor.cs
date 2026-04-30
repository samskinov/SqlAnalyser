using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlAnalyzer.Core.Models;

namespace SqlAnalyzer.Core.Parsing
{
    /// <summary>
    /// Walks a ScriptDom AST fragment and extracts every multi-part identifier
    /// that refers to a named object (table, view, procedure, function…).
    ///
    /// Strategy:
    ///   - Visit all NamedTableReference  → covers FROM / JOIN targets.
    ///   - Visit all ProcedureReferences  → covers EXEC / EXECUTE calls.
    ///   - Visit all FunctionCallExpressions → covers scalar / TVF calls.
    ///   - Visit all SchemaObjectFunctionTableReference → covers TVF in FROM.
    ///
    /// The type is NOT set here (Unknown) — it is resolved later via metadata.
    /// </summary>
    public sealed class ObjectReferenceExtractor : TSqlFragmentVisitor
    {
        private readonly string _defaultDatabase;
        private readonly string _batchContext;
        private readonly List<SqlObjectReference> _references = new List<SqlObjectReference>();

        // O(1) deduplication keyed on ResolutionKey.
        private readonly HashSet<string> _referenceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Built-in function names to skip (system functions, not user objects).
        // Deduplicated; clause keywords (OVER, PARTITION…) excluded as they are not callables.
        private static readonly HashSet<string> BuiltInFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "COALESCE","NULLIF","IIF","CHOOSE","ISNULL","ISNUMERIC","ISDATE",
            "CAST","CONVERT","TRY_CAST","TRY_CONVERT","TRY_PARSE","PARSE",
            "GETDATE","GETUTCDATE","SYSDATETIME","SYSUTCDATETIME","NEWID","NEWSEQUENTIALID",
            "LEN","LTRIM","RTRIM","TRIM","UPPER","LOWER","SUBSTRING","CHARINDEX","REPLACE","STUFF",
            "LEFT","RIGHT","REVERSE","REPLICATE","SPACE","ASCII","CHAR","UNICODE","NCHAR",
            "STR","FORMAT","CONCAT","CONCAT_WS","STRING_AGG","STRING_SPLIT","TRANSLATE",
            "PATINDEX","SOUNDEX","DIFFERENCE","QUOTENAME","PARSENAME",
            "ABS","CEILING","FLOOR","ROUND","POWER","SQRT","SQUARE","LOG","LOG10","EXP",
            "SIGN","SIN","COS","TAN","ASIN","ACOS","ATAN","ATN2","PI","RAND",
            "COUNT","SUM","AVG","MIN","MAX","STDEV","VAR","CHECKSUM_AGG","COUNT_BIG",
            "ROW_NUMBER","RANK","DENSE_RANK","NTILE","LAG","LEAD","FIRST_VALUE","LAST_VALUE",
            "DATEADD","DATEDIFF","DATEDIFF_BIG","DATENAME","DATEPART","DATEFROMPARTS",
            "DATETIMEFROMPARTS","TIMEFROMPARTS","EOMONTH","SWITCHOFFSET","TODATETIMEOFFSET",
            "YEAR","MONTH","DAY",
            "OBJECT_ID","OBJECT_NAME","OBJECT_DEFINITION","SCHEMA_NAME","SCHEMA_ID",
            "DB_NAME","DB_ID","USER_NAME","USER_ID","SUSER_NAME","SUSER_ID",
            "HAS_PERMS_BY_NAME","PERMISSIONS","IS_MEMBER","IS_ROLEMEMBER","IS_SRVROLEMEMBER",
            "HOST_NAME","APP_NAME","@@ROWCOUNT","@@IDENTITY","SCOPE_IDENTITY","@@ERROR",
            "@@TRANCOUNT","@@SPID","@@SERVERNAME","@@VERSION","@@LANGUAGE","@@DATEFIRST",
            "COMPRESS","DECOMPRESS","HASHBYTES","PWDCOMPARE","PWDENCRYPT",
            "OPENROWSET","OPENQUERY","OPENDATASOURCE","OPENXML","OPENJSON",
            "JSON_VALUE","JSON_QUERY","JSON_MODIFY","ISJSON",
            "COLUMNPROPERTY","TYPEPROPERTY","OBJECTPROPERTY","OBJECTPROPERTYEX",
            "INDEXPROPERTY","FILEGROUPPROPERTY","FILEPROPERTY","FULLTEXTCATALOGPROPERTY",
            "SERVERPROPERTY","CONNECTIONPROPERTY","SESSIONPROPERTY",
            "FORMATMESSAGE","RAISERROR","THROW","ERROR_MESSAGE","ERROR_NUMBER",
            "ERROR_SEVERITY","ERROR_STATE","ERROR_LINE","ERROR_PROCEDURE",
            "XACT_STATE","TRANCOUNT",
            "GENERATE_SERIES","GREATEST","LEAST","DATE_BUCKET"
        };

        // System schema prefixes to skip.
        private static readonly HashSet<string> SystemSchemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sys", "INFORMATION_SCHEMA", "guest"
        };

        public ObjectReferenceExtractor(string batchContext, string defaultDatabase)
        {
            _batchContext = batchContext ?? string.Empty;
            _defaultDatabase = defaultDatabase ?? string.Empty;
        }

        public IReadOnlyList<SqlObjectReference> References => _references.AsReadOnly();

        // ------------------------------------------------------------------ AST visitors

        public override void Visit(NamedTableReference node)
        {
            ExtractFromSchemaObject(node?.SchemaObject);
            base.Visit(node);
        }

        public override void Visit(SchemaObjectFunctionTableReference node)
        {
            ExtractFromSchemaObject(node?.SchemaObject);
            base.Visit(node);
        }

        public override void Visit(ExecutableProcedureReference node)
        {
            if (node?.ProcedureReference?.ProcedureVariable == null)
                ExtractFromSchemaObject(node?.ProcedureReference?.ProcedureReference?.Name);
            base.Visit(node);
        }

        public override void Visit(FunctionCall node)
        {
            // Multi-part schema-qualified scalar calls like dbo.fn_Foo(...)
            if (node?.CallTarget is MultiPartIdentifierCallTarget mpi && mpi.MultiPartIdentifier != null)
            {
                var identifiers = mpi.MultiPartIdentifier.Identifiers;
                if (identifiers.Count >= 2)
                {
                    string objectName = identifiers[identifiers.Count - 1].Value;
                    string schema     = identifiers[identifiers.Count - 2].Value;
                    string? database  = identifiers.Count >= 3 ? identifiers[identifiers.Count - 3].Value : null;
                    string? server    = identifiers.Count >= 4 ? identifiers[identifiers.Count - 4].Value : null;

                    if (!string.IsNullOrWhiteSpace(objectName) && !BuiltInFunctions.Contains(objectName))
                    {
                        TryAdd(new SqlObjectReference
                        {
                            Server     = string.IsNullOrWhiteSpace(server)   ? null : server,
                            Database   = string.IsNullOrWhiteSpace(database) ? null : database,
                            Schema     = string.IsNullOrWhiteSpace(schema)   ? null : schema,
                            ObjectName = objectName,
                            ActiveDatabaseContext = _batchContext
                        });
                    }
                }
            }
            base.Visit(node);
        }

        // ------------------------------------------------------------------ core extraction

        private void ExtractFromSchemaObject(SchemaObjectName? schemaObj)
        {
            if (schemaObj is null) return;

            string objectName = schemaObj.BaseIdentifier?.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(objectName)) return;

            string? schema   = schemaObj.SchemaIdentifier?.Value;
            string? database = schemaObj.DatabaseIdentifier?.Value;
            string? server   = schemaObj.ServerIdentifier?.Value;

            if (!string.IsNullOrEmpty(schema) && SystemSchemas.Contains(schema)) return;
            if (objectName.StartsWith("#")) return;
            if (BuiltInFunctions.Contains(objectName)) return;

            TryAdd(new SqlObjectReference
            {
                Server     = string.IsNullOrWhiteSpace(server)   ? null : server,
                Database   = string.IsNullOrWhiteSpace(database) ? null : database,
                Schema     = string.IsNullOrWhiteSpace(schema)   ? null : schema,
                ObjectName = objectName,
                ActiveDatabaseContext = _batchContext
            });
        }

        private void TryAdd(SqlObjectReference reference)
        {
            if (_referenceKeys.Add(reference.ResolutionKey))
                _references.Add(reference);
        }

        // ------------------------------------------------------------------ static entry point

        /// <summary>
        /// Extracts all object references from the given T-SQL batch.
        /// Reuses <see cref="ScriptBatch.ParsedFragment"/> when available to avoid re-parsing.
        /// </summary>
        public static List<SqlObjectReference> Extract(ScriptBatch batch, string defaultDatabase)
        {
            if (batch is null) throw new ArgumentNullException(nameof(batch));

            if (string.IsNullOrWhiteSpace(batch.BatchText))
                return new List<SqlObjectReference>();

            // Reuse the pre-parsed fragment stored by SqlScriptParser (avoids double-parse).
            TSqlFragment? fragment = batch.ParsedFragment;
            if (fragment is null)
            {
                IList<ParseError> errors;
                var parser = new TSql160Parser(initialQuotedIdentifiers: true);
                using (var reader = new StringReader(batch.BatchText))
                    fragment = parser.Parse(reader, out errors);
                if (fragment is null) return new List<SqlObjectReference>();
            }

            var extractor = new ObjectReferenceExtractor(batch.DatabaseContext, defaultDatabase);
            fragment.Accept(extractor);
            return new List<SqlObjectReference>(extractor.References);
        }
    }
}

