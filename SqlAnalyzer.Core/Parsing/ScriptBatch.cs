using System.Collections.Generic;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlAnalyzer.Core.Parsing
{
    /// <summary>
    /// One T-SQL batch (text between GO separators) with its associated database context.
    /// </summary>
    public sealed class ScriptBatch
    {
        /// <summary>Text of the batch (without the GO keyword).</summary>
        public string BatchText { get; set; } = string.Empty;

        /// <summary>Database context active at the start of this batch (may have changed via USE).</summary>
        public string DatabaseContext { get; set; } = string.Empty;

        /// <summary>Zero-based index of this batch in the original script.</summary>
        public int BatchIndex { get; set; }

        /// <summary>Parse errors detected for this batch, if any.</summary>
        public List<string> ParseErrors { get; set; } = new List<string>();

        /// <summary>
        /// Pre-parsed ScriptDom AST fragment for this batch.
        /// Set by <see cref="SqlScriptParser"/> and reused by <see cref="ObjectReferenceExtractor"/>
        /// to avoid double-parsing the same batch text.
        /// </summary>
        public TSqlFragment? ParsedFragment { get; set; }
    }
}
