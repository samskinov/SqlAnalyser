using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlAnalyzer.Core.Parsing
{
    /// <summary>
    /// Splits a T-SQL script into batches separated by GO, applies USE-statement
    /// tracking to maintain the active database context per batch, and parses each
    /// batch with ScriptDom to provide a validated AST fragment.
    ///
    /// GO detection uses ScriptDom's own batch-aware parser so that GO occurrences
    /// inside block comments, line comments, or string literals are never misidentified
    /// as batch separators.
    /// </summary>
    public sealed class SqlScriptParser
    {
        private readonly TSql160Parser _parser = new TSql160Parser(initialQuotedIdentifiers: true);

        /// <summary>
        /// Parses a SQL script file from disk into a list of <see cref="ScriptBatch"/> objects.
        /// Encoding is auto-detected from the byte-order mark (BOM); falls back to UTF-8.
        /// </summary>
        /// <param name="filePath">Absolute path to the .sql file.</param>
        /// <param name="defaultDatabase">Initial database context before any USE statement.</param>
        public List<ScriptBatch> ParseFile(string filePath, string defaultDatabase)
        {
            if (filePath is null) throw new ArgumentNullException(nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"SQL script file not found: {filePath}", filePath);

            string scriptContent;
            using (var sr = new StreamReader(filePath, detectEncodingFromByteOrderMarks: true))
                scriptContent = sr.ReadToEnd();

            return ParseText(scriptContent, defaultDatabase);
        }

        /// <summary>
        /// Parses raw T-SQL text into a list of <see cref="ScriptBatch"/> objects.
        /// </summary>
        /// <param name="scriptContent">Raw T-SQL text.</param>
        /// <param name="defaultDatabase">Initial database context.</param>
        public List<ScriptBatch> ParseText(string scriptContent, string defaultDatabase)
        {
            if (scriptContent is null) throw new ArgumentNullException(nameof(scriptContent));
            return SplitIntoBatches(scriptContent, defaultDatabase);
        }

        // ------------------------------------------------------------------ private helpers

        private List<ScriptBatch> SplitIntoBatches(string script, string defaultDatabase)
        {
            var result = new List<ScriptBatch>();
            string currentDb = defaultDatabase ?? string.Empty;

            // Parse the entire script in one pass.  ScriptDom correctly identifies GO
            // batch separators even when "GO" appears inside comments or string literals.
            IList<ParseError> errors;
            TSqlScript? tsqlScript;
            using (var reader = new StringReader(script))
                tsqlScript = _parser.Parse(reader, out errors) as TSqlScript;

            // Collect script-level parse error messages (attached to the first batch below).
            var errorMessages = new List<string>();
            if (errors != null)
                foreach (ParseError err in errors)
                    errorMessages.Add($"[Line {err.Line}, Col {err.Column}] {err.Message}");

            if (tsqlScript == null || tsqlScript.Batches.Count == 0)
            {
                // Parser returned nothing useful; emit one synthetic batch so errors are visible.
                if (!string.IsNullOrWhiteSpace(script))
                {
                    var fallback = new ScriptBatch
                    {
                        BatchText       = script.Trim(),
                        DatabaseContext = currentDb,
                        BatchIndex      = 0,
                        ParsedFragment  = null
                    };
                    fallback.ParseErrors.AddRange(errorMessages);
                    result.Add(fallback);
                }
                return result;
            }

            int batchIndex = 0;
            foreach (TSqlBatch tsqlBatch in tsqlScript.Batches)
            {
                // Recover original batch text from character offsets preserved by ScriptDom.
                int start = tsqlBatch.StartOffset;
                int len   = tsqlBatch.FragmentLength;
                string batchText = (start >= 0 && len > 0 && start + len <= script.Length)
                    ? script.Substring(start, len).Trim()
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(batchText))
                {
                    batchIndex++;
                    continue;
                }

                // Detect any USE statement in this batch via the already-parsed AST.
                string newDb = ExtractNewDatabaseFromBatch(tsqlBatch, currentDb);

                var batch = new ScriptBatch
                {
                    BatchText       = batchText,
                    DatabaseContext = currentDb,   // context active when this batch starts
                    BatchIndex      = batchIndex,
                    ParsedFragment  = tsqlBatch    // reused by ObjectReferenceExtractor
                };

                // Attach script-level parse errors to the first non-empty batch.
                if (batchIndex == 0 && errorMessages.Count > 0)
                    batch.ParseErrors.AddRange(errorMessages);

                result.Add(batch);
                currentDb = newDb;  // update context after this batch executes
                batchIndex++;
            }

            return result;
        }

        private string ExtractNewDatabaseFromBatch(TSqlBatch tsqlBatch, string current)
        {
            var visitor = new UseStatementVisitor();
            tsqlBatch.Accept(visitor);
            return visitor.DatabaseName ?? current;
        }

        // AST visitor that captures the target database of a USE statement.
        private sealed class UseStatementVisitor : TSqlFragmentVisitor
        {
            public string? DatabaseName { get; private set; }

            public override void Visit(UseStatement node)
            {
                string? name = node.DatabaseName?.Value;
                if (!string.IsNullOrWhiteSpace(name))
                    DatabaseName = name;
            }
        }
    }
}

