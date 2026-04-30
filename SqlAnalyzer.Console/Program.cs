using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SqlAnalyzer.Core.Models;
using SqlAnalyzer.Core.Orchestration;
using SqlAnalyzer.Core.Prompting;

namespace SqlAnalyzer.Console
{
    /// <summary>
    /// Console demo for SqlAnalyzer.Core.
    ///
    /// Usage:
    ///   SqlAnalyzer.Console.exe [options] file1.sql [file2.sql ...]
    ///
    /// Options:
    ///   --server       SQL Server host (default: .)
    ///   --database     Default database / initial catalog (required)
    ///   --user         SQL login (omit for Windows auth)
    ///   --password     SQL password (omit for Windows auth)
    ///   --depth        Max dependency depth (default: 5)
    ///   --no-scripts   Exclude original scripts from prompt output
    ///   --output       Path to write the prompt context file (default: stdout)
    ///
    /// Example:
    ///   SqlAnalyzer.Console.exe --server MYSERVER --database MyDB --depth 3 query.sql
    /// </summary>
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
            {
                PrintHelp();
                return 0;
            }

            try
            {
                var options = ParseArguments(args, out List<string> sqlFiles);

                if (sqlFiles.Count == 0)
                {
                    System.Console.Error.WriteLine("ERROR: No .sql files specified.");
                    PrintHelp();
                    return 1;
                }

                // Validate files exist.
                foreach (string f in sqlFiles)
                {
                    if (!File.Exists(f))
                    {
                        System.Console.Error.WriteLine($"ERROR: File not found: {f}");
                        return 1;
                    }
                }

                System.Console.WriteLine($"[*] Analysing {sqlFiles.Count} file(s) against database '{options.DefaultDatabase}'...");

                // -------------------------------------------------------
                // MODE A — pass AnalysisOptions (connection string owned by lib)
                // var orchestrator = new SqlAnalysisOrchestrator(options);
                //
                // MODE B — pass an existing SqlConnection from the parent app.
                //   The orchestrator reuses the server / auth details and swaps
                //   the catalog automatically for multi-database resolution.
                // -------------------------------------------------------

                // The console demo always owns its connection, so we open one and pass it.
                using (var parentConnection = new SqlConnection(options.BaseConnectionString!))
                {
                    parentConnection.Open();

                    // Pass the open connection + the default database.
                    // Optional AnalysisOptions carries tuning parameters (depth, timeouts…).
                    var tuning = new AnalysisOptions
                    {
                        MaxDependencyDepth           = options.MaxDependencyDepth,
                        ResolveRecursiveDependencies = options.ResolveRecursiveDependencies,
                        CommandTimeoutSeconds        = options.CommandTimeoutSeconds,
                        ConnectionTimeoutSeconds     = options.ConnectionTimeoutSeconds
                    };

                    var orchestrator = new SqlAnalysisOrchestrator(
                        parentConnection,
                        options.DefaultDatabase!,
                        tuning);

                    var result = await orchestrator.AnalyseAsync(sqlFiles);

                    PrintSummary(result);

                    bool includeScripts = !args.Contains("--no-scripts", StringComparer.OrdinalIgnoreCase);
                    var builder = new PromptBuilder();
                    string promptContext = builder.Build(result, includeOriginalScripts: includeScripts);

                    string? outputPath = GetOption(args, "--output");
                    if (!string.IsNullOrWhiteSpace(outputPath))
                    {
                        File.WriteAllText(outputPath!, promptContext, System.Text.Encoding.UTF8);
                        System.Console.WriteLine($"[*] Prompt context written to: {outputPath}");
                    }
                    else
                    {
                        System.Console.WriteLine();
                        System.Console.WriteLine("===== PROMPT CONTEXT =====");
                        System.Console.WriteLine(promptContext);
                    }

                    return result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) ? 2 : 0;
                }
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"FATAL: {ex.Message}");
                return 99;
            }
        }

        // ------------------------------------------------------------------ helpers

        private static void PrintSummary(AnalysisResult result)
        {
            System.Console.WriteLine();
            System.Console.WriteLine("===== ANALYSIS SUMMARY =====");
            System.Console.WriteLine($"  Resolved objects  : {result.ResolvedObjects.Count}");
            System.Console.WriteLine($"  Unresolved refs   : {result.UnresolvedObjects.Count}");
            System.Console.WriteLine($"  Dependency roots  : {result.DependencyGraph.Count}");
            System.Console.WriteLine($"  Diagnostics       : {result.Diagnostics.Count}");

            if (result.ResolvedObjects.Count > 0)
            {
                System.Console.WriteLine();
                System.Console.WriteLine("  Resolved:");
                foreach (var kv in result.ResolvedObjects
                    .OrderBy(x => x.Value.DiscoveryDepth)
                    .ThenBy(x => x.Value.Reference.FullName))
                {
                    string type = kv.Value.ObjectType.ToString().PadRight(22);
                    string depth = $"(depth {kv.Value.DiscoveryDepth})";
                    string flags = kv.Value.IsEncrypted ? " [ENCRYPTED]"
                        : kv.Value.DefinitionIsNull ? " [NO-DEF]"
                        : string.Empty;
                    System.Console.WriteLine($"    {type} {kv.Value.Reference.FullName} {depth}{flags}");
                }
            }

            if (result.UnresolvedObjects.Count > 0)
            {
                System.Console.WriteLine();
                System.Console.WriteLine("  Unresolved:");
                foreach (var u in result.UnresolvedObjects.OrderBy(x => x.Reference.FullName))
                    System.Console.WriteLine($"    {u.Reference.FullName} — {u.Reason}");
            }

            if (result.Diagnostics.Count > 0)
            {
                System.Console.WriteLine();
                System.Console.WriteLine("  Diagnostics:");
                foreach (var diag in result.Diagnostics)
                    System.Console.WriteLine($"    {diag}");
            }

            System.Console.WriteLine();
        }

        private static readonly HashSet<string> _valueOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "--server", "--database", "--user", "--password", "--depth", "--output" };

        private static AnalysisOptions ParseArguments(string[] args, out List<string> sqlFiles)
        {
            string server   = GetOption(args, "--server")   ?? ".";
            string database = GetOption(args, "--database") ?? throw new ArgumentException("--database is required.");
            string? user     = GetOption(args, "--user");
            string? password = GetOption(args, "--password");
            int depth        = int.TryParse(GetOption(args, "--depth"), out int d) ? d : 5;

            string connStr = BuildConnectionString(server, database, user, password);

            sqlFiles = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--"))
                {
                    // Skip value-taking options and their value token.
                    if (_valueOptions.Contains(args[i]) && i + 1 < args.Length)
                        i++;
                    // Boolean flags (e.g. --no-scripts) are consumed here without taking a value.
                    continue;
                }
                sqlFiles.Add(args[i]);
            }

            return new AnalysisOptions
            {
                DefaultDatabase = database,
                BaseConnectionString = connStr,
                MaxDependencyDepth = depth,
                ResolveRecursiveDependencies = true
            };
        }

        private static string BuildConnectionString(string server, string database, string? user, string? password)
        {
            var sb = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = database,
                TrustServerCertificate = true
            };

            if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(password))
            {
                sb.UserID = user!;
                sb.Password = password!;
            }
            else
            {
                sb.IntegratedSecurity = true;
            }

            return sb.ConnectionString;
        }

        private static string? GetOption(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }

        private static void PrintHelp()
        {
            System.Console.WriteLine(
                "SqlAnalyzer.Console — T-SQL script analyser\n" +
                "\n" +
                "Usage:\n" +
                "  SqlAnalyzer.Console.exe [options] file1.sql [file2.sql ...]\n" +
                "\n" +
                "Options:\n" +
                "  --server    <host>      SQL Server host (default: .)\n" +
                "  --database  <name>      Default catalog (required)\n" +
                "  --user      <login>     SQL login (omit for Windows auth)\n" +
                "  --password  <pwd>       SQL password\n" +
                "  --depth     <n>         Max dependency depth (default: 5)\n" +
                "  --no-scripts            Exclude original scripts from prompt output\n" +
                "  --output    <file>      Write prompt context to file (default: stdout)\n");
        }
    }
}
