using System;

namespace SqlAnalyzer.Core.Models
{
    /// <summary>
    /// A raw reference to a SQL object extracted from T-SQL syntax.
    /// May be unresolved (type unknown, catalog may be partial).
    /// </summary>
    public sealed class SqlObjectReference : IEquatable<SqlObjectReference>
    {
        /// <summary>Linked server name (4-part name), may be null.</summary>
        public string? Server { get; set; }

        /// <summary>Catalog/database name as written in the script, may be null if unqualified.</summary>
        public string? Database { get; set; }

        /// <summary>Schema name, defaults to "dbo" when absent and resolved.</summary>
        public string? Schema { get; set; }

        /// <summary>Object name, never null.</summary>
        public string ObjectName { get; set; } = string.Empty;

        /// <summary>The database context active when this reference was found (from USE / default).</summary>
        public string ActiveDatabaseContext { get; set; } = string.Empty;

        /// <summary>The database that should actually be queried to resolve this reference.</summary>
        public string EffectiveDatabase => !string.IsNullOrWhiteSpace(Database) ? Database : ActiveDatabaseContext;

        /// <summary>Fully qualified name as found in the script.</summary>
        public string FullName
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>(4);
                if (!string.IsNullOrWhiteSpace(Server)) parts.Add(Quote(Server));
                if (!string.IsNullOrWhiteSpace(Database)) parts.Add(Quote(Database));
                if (!string.IsNullOrWhiteSpace(Schema)) parts.Add(Quote(Schema));
                parts.Add(Quote(ObjectName));
                return string.Join(".", parts);
            }
        }

        // Cached resolution key — computed once on first access since properties are
        // set during object initialisation and never mutated afterwards.
        private string? _resolutionKey;

        /// <summary>Unique resolution key: database.schema.object (lower-case, normalised).</summary>
        public string ResolutionKey =>
            _resolutionKey ??=
                $"{EffectiveDatabase?.ToLowerInvariant() ?? ""}.{(Schema ?? "dbo").ToLowerInvariant()}.{ObjectName?.ToLowerInvariant() ?? ""}";

        private static string Quote(string? name) =>
            string.IsNullOrEmpty(name) ? string.Empty : $"[{name!.Trim('[', ']')}]";

        public bool Equals(SqlObjectReference? other)
        {
            if (other is null) return false;
            return string.Equals(ResolutionKey, other.ResolutionKey, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object? obj) => Equals(obj as SqlObjectReference);
        public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(ResolutionKey);
        public override string ToString() => FullName;
    }
}
