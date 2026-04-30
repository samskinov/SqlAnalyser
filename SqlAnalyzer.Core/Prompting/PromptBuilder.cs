using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SqlAnalyzer.Core.Models;

namespace SqlAnalyzer.Core.Prompting
{
    /// <summary>
    /// Builds the SQL context block that is injected into an AI documentation prompt.
    ///
    /// The caller is expected to have their own prompt template.
    /// This class produces the DATA section that should be embedded within that template:
    ///
    ///   ---SQL CONTEXT BEGIN---
    ///   ### ORIGINAL SCRIPT(S)
    ///   ...
    ///   ### RESOLVED OBJECTS (by discovery depth)
    ///   ...
    ///   ### UNRESOLVED REFERENCES
    ///   ...
    ///   ---SQL CONTEXT END---
    ///
    /// The AI receives:
    ///   - the original T-SQL that triggered the analysis,
    ///   - the formatted definition of every view / stored proc / function found,
    ///   - the dependency graph showing which object calls which,
    ///   - a note for each object that could not be retrieved.
    /// </summary>
    public sealed class PromptBuilder
    {
        // ------------------------------------------------------------------ public API

        /// <summary>
        /// Builds the SQL context block from an <see cref="AnalysisResult"/>.
        /// </summary>
        /// <param name="result">Completed analysis result.</param>
        /// <param name="includeOriginalScripts">
        ///   When true, the raw original SQL scripts are included in the output.
        ///   Set to false if the scripts are very large and you only need the resolved definitions.
        /// </param>
        /// <param name="includeUnresolved">
        ///   When true, a section lists all objects that could not be resolved.
        /// </param>
        /// <returns>A plain-text block ready to be injected into your prompt template.</returns>
        public string Build(
            AnalysisResult result,
            bool includeOriginalScripts = true,
            bool includeUnresolved = true)
        {
            if (result is null) throw new ArgumentNullException(nameof(result));

            var sb = new StringBuilder();

            sb.AppendLine("---SQL CONTEXT BEGIN---");
            sb.AppendLine();

            // ---- Original scripts ----
            if (includeOriginalScripts && result.OriginalScripts.Count > 0)
            {
                sb.AppendLine("### ORIGINAL SCRIPT(S)");
                sb.AppendLine();
                foreach (var kv in result.OriginalScripts)
                {
                    string label = Path.GetFileName(kv.Key);
                    sb.AppendLine($"#### File: {label}");
                    sb.AppendLine("```sql");
                    sb.AppendLine(kv.Value.Trim());
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }

            // ---- Dependency graph summary ----
            sb.AppendLine("### DEPENDENCY GRAPH");
            sb.AppendLine();
            sb.AppendLine("The following tree shows which objects were referenced directly by the");
            sb.AppendLine("original script(s) and which objects they in turn depend on.");
            sb.AppendLine();

            if (result.DependencyGraph.Count == 0)
            {
                sb.AppendLine("No documentable objects found.");
            }
            else
            {
                foreach (var root in result.DependencyGraph)
                    AppendDependencyTree(sb, root, depth: 0);
            }

            sb.AppendLine();

            // ---- Resolved definitions grouped by depth ----
            sb.AppendLine("### RESOLVED OBJECT DEFINITIONS");
            sb.AppendLine();

            var grouped = result.ResolvedObjects.Values
                .Where(o => IsDocumentable(o.ObjectType))
                .OrderBy(o => o.DiscoveryDepth)
                .ThenBy(o => o.Reference.EffectiveDatabase, StringComparer.OrdinalIgnoreCase)
                .ThenBy(o => o.Reference.ObjectName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (grouped.Count == 0)
            {
                sb.AppendLine("No documentable definitions could be retrieved.");
            }
            else
            {
                int currentDepth = -1;
                foreach (var obj in grouped)
                {
                    if (obj.DiscoveryDepth != currentDepth)
                    {
                        currentDepth = obj.DiscoveryDepth;
                        string depthLabel = currentDepth == 0
                            ? "Directly referenced (depth 0)"
                            : $"Transitive dependency (depth {currentDepth})";
                        sb.AppendLine($"#### {depthLabel}");
                        sb.AppendLine();
                    }

                    AppendObjectDefinition(sb, obj);
                }
            }

            // ---- Unresolved references ----
            if (includeUnresolved && result.UnresolvedObjects.Count > 0)
            {
                sb.AppendLine("### UNRESOLVED REFERENCES");
                sb.AppendLine();
                sb.AppendLine("The following references were detected but could not be fully resolved.");
                sb.AppendLine("This may be because the object does not exist in the connected SQL Server,");
                sb.AppendLine("the account lacks VIEW DEFINITION permission, or the object is a temp table / CTE / alias.");
                sb.AppendLine();

                foreach (var u in result.UnresolvedObjects.OrderBy(x => x.Reference.FullName))
                {
                    sb.AppendLine($"- `{u.Reference.FullName}` — {u.Reason}");
                }

                sb.AppendLine();
            }

            sb.AppendLine("---SQL CONTEXT END---");

            return sb.ToString();
        }

        // ------------------------------------------------------------------ private helpers

        private static void AppendDependencyTree(StringBuilder sb, DependencyNode node, int depth)
        {
            string indent = new string(' ', depth * 4);
            string icon = node.Object.ObjectType switch
            {
                SqlObjectType.View => "[VIEW]",
                SqlObjectType.StoredProcedure => "[PROC]",
                SqlObjectType.ScalarFunction => "[FUNC]",
                SqlObjectType.TableValuedFunction => "[TVF] ",
                SqlObjectType.InlineTableValuedFunction => "[TVF] ",
                SqlObjectType.Table => "[TABLE]",
                _ => "[?]  "
            };

            string notes = node.Object.IsEncrypted ? " ⚠ encrypted"
                : node.Object.DefinitionIsNull ? " ⚠ definition null"
                : string.Empty;

            sb.AppendLine($"{indent}{icon} {node.Object.Reference.FullName}{notes}");

            foreach (var child in node.Children)
                AppendDependencyTree(sb, child, depth + 1);
        }

        private static void AppendObjectDefinition(StringBuilder sb, ResolvedSqlObject obj)
        {
            string typeName = obj.ObjectType.ToString().ToUpperInvariant();
            string qualifiedName = obj.Reference.FullName;
            string database = obj.Reference.EffectiveDatabase;

            sb.AppendLine($"##### {typeName}: `{qualifiedName}`");
            sb.AppendLine($"- **Database**: `{database}`");
            sb.AppendLine($"- **Schema**: `{obj.Reference.Schema ?? "dbo"}`");
            sb.AppendLine($"- **Discovery depth**: {obj.DiscoveryDepth}");

            if (!string.IsNullOrWhiteSpace(obj.Notes))
                sb.AppendLine($"- **Note**: {obj.Notes}");

            if (obj.IsEncrypted)
            {
                sb.AppendLine();
                sb.AppendLine("> ⚠ This object is encrypted. Its definition cannot be retrieved.");
                sb.AppendLine();
                return;
            }

            if (obj.DefinitionIsNull || string.IsNullOrWhiteSpace(obj.FormattedDefinition ?? obj.RawDefinition))
            {
                sb.AppendLine();
                sb.AppendLine("> ⚠ No definition available (NULL in sys.sql_modules or not a scriptable object).");
                sb.AppendLine();
                return;
            }

            string defToShow = !string.IsNullOrWhiteSpace(obj.FormattedDefinition)
                ? obj.FormattedDefinition
                : obj.RawDefinition!;

            sb.AppendLine();
            sb.AppendLine("```sql");
            sb.AppendLine(defToShow.Trim());
            sb.AppendLine("```");
            sb.AppendLine();
        }

        private static bool IsDocumentable(SqlObjectType type)
        {
            return type == SqlObjectType.View
                || type == SqlObjectType.StoredProcedure
                || type == SqlObjectType.ScalarFunction
                || type == SqlObjectType.TableValuedFunction
                || type == SqlObjectType.InlineTableValuedFunction
                || type == SqlObjectType.Trigger;
        }
    }
}
