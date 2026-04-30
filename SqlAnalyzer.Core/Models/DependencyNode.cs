using System.Collections.Generic;

namespace SqlAnalyzer.Core.Models
{
    /// <summary>
    /// Node in the dependency graph for a resolved SQL object.
    /// </summary>
    public sealed class DependencyNode
    {
        public ResolvedSqlObject Object { get; set; } = null!;
        public List<DependencyNode> Children { get; set; } = new List<DependencyNode>();
    }
}
