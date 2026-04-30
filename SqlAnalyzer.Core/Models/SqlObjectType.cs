namespace SqlAnalyzer.Core.Models
{
    /// <summary>
    /// Represents the SQL object types we care about discovering.
    /// </summary>
    public enum SqlObjectType
    {
        Unknown = 0,
        View = 1,
        StoredProcedure = 2,
        ScalarFunction = 3,
        TableValuedFunction = 4,
        InlineTableValuedFunction = 5,
        Table = 6,          // Detected but not documented
        Synonym = 7,
        Trigger = 8
    }
}
