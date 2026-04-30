using System.Collections.Generic;

namespace SqlAnalyzer.Core.Models
{
    /// <summary>
    /// Full analysis result returned by the orchestrator.
    /// </summary>
    public sealed class AnalysisResult
    {
        /// <summary>All resolved objects, keyed by their resolution key (db.schema.object).</summary>
        public Dictionary<string, ResolvedSqlObject> ResolvedObjects { get; }
            = new Dictionary<string, ResolvedSqlObject>();

        /// <summary>Dependency graph roots (objects referenced directly in the original script).</summary>
        public List<DependencyNode> DependencyGraph { get; } = new List<DependencyNode>();

        /// <summary>Objects that could not be resolved, with the reason.</summary>
        public List<UnresolvedObject> UnresolvedObjects { get; } = new List<UnresolvedObject>();

        /// <summary>Structured diagnostics collected during the analysis.</summary>
        public List<Diagnostic> Diagnostics { get; } = new List<Diagnostic>();

        /// <summary>
        /// Original SQL scripts that were analysed (file path → content).
        /// Stored as an ordered list to guarantee deterministic iteration order in output.
        /// </summary>
        public List<KeyValuePair<string, string>> OriginalScripts { get; }
            = new List<KeyValuePair<string, string>>();
    }
}
