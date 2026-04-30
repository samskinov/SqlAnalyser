namespace SqlAnalyzer.Core.Models
{
    /// <summary>
    /// Represents a SQL object reference that could not be resolved.
    /// </summary>
    public sealed class UnresolvedObject
    {
        public SqlObjectReference Reference { get; set; } = null!;
        public string Reason { get; set; } = string.Empty;
    }
}
