using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlAnalyzer.Core.Models;
namespace SqlAnalyzer.Core.Formatting
{
    /// <summary>
    /// Re-formats raw T-SQL definitions retrieved from sys.sql_modules using ScriptDom.
    ///
    /// Strategy:
    ///   1. Parse the raw definition with TSql160Parser.
    ///   2. If parsing succeeds with no errors, emit via SqlScriptGenerator with
    ///      standard formatting options.
    ///   3. If parsing fails (e.g. vendor-specific or undocumented syntax),
    ///      fall back to the raw definition with basic whitespace normalisation.
    /// </summary>
    public sealed class SqlDefinitionFormatter
    {
        private static readonly SqlScriptGeneratorOptions GeneratorOptions = new SqlScriptGeneratorOptions
        {
            SqlVersion = SqlVersion.Sql160,
            KeywordCasing = KeywordCasing.Uppercase,
            IndentationSize = 4,
            IncludeSemicolons = false,
            AlignColumnDefinitionFields = true,
            AlignSetClauseItem = true,
            AsKeywordOnOwnLine = false
        };

        private readonly TSql160Parser _parser = new TSql160Parser(initialQuotedIdentifiers: true);

        // ------------------------------------------------------------------ public API

        /// <summary>
        /// Formats a raw sys.sql_modules definition.
        /// Never throws; records issues in <paramref name="diagnostics"/>.
        /// </summary>
        /// <param name="rawDefinition">Definition from sys.sql_modules.definition.</param>
        /// <param name="objectName">Human-readable name for diagnostics.</param>
        /// <param name="diagnostics">Accumulator for warnings.</param>
        /// <returns>Formatted SQL, or the raw definition if formatting is not possible.</returns>
        public string Format(string rawDefinition, string objectName, List<Diagnostic> diagnostics)
        {
            diagnostics ??= new List<Diagnostic>();

            if (string.IsNullOrWhiteSpace(rawDefinition))
                return rawDefinition ?? string.Empty;

            try
            {
                TSqlFragment fragment;
                IList<ParseError> errors;

                using (var reader = new StringReader(rawDefinition))
                    fragment = _parser.Parse(reader, out errors);

                if (errors != null && errors.Count > 0)
                {
                    foreach (var err in errors)
                        diagnostics.Add(Diagnostic.Warning($"Format parse error for '{objectName}' [{err.Line}:{err.Column}]: {err.Message}"));

                    return NormaliseWhitespace(rawDefinition);
                }

                var generator = new Sql160ScriptGenerator(GeneratorOptions);
                generator.GenerateScript(fragment, out string script);

                return string.IsNullOrWhiteSpace(script) ? NormaliseWhitespace(rawDefinition) : script;
            }
            catch (Exception ex)
            {
                diagnostics.Add(Diagnostic.Warning($"Could not format definition for '{objectName}': {ex.Message}"));
                return NormaliseWhitespace(rawDefinition);
            }
        }

        // ------------------------------------------------------------------ private helpers

        /// <summary>
        /// Basic whitespace normalisation fallback:
        ///  - normalise line endings,
        ///  - remove leading/trailing blank lines,
        ///  - collapse sequences of more than 2 consecutive blank lines.
        /// </summary>
        private static string NormaliseWhitespace(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return sql;

            string[] lines = sql.Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder(sql.Length);
            int consecutiveBlank = 0;

            foreach (string line in lines)
            {
                bool isBlank = string.IsNullOrWhiteSpace(line);

                if (isBlank)
                {
                    consecutiveBlank++;
                    if (consecutiveBlank <= 2) sb.AppendLine();
                }
                else
                {
                    consecutiveBlank = 0;
                    sb.AppendLine(line);
                }
            }

            return sb.ToString().Trim();
        }
    }
}
