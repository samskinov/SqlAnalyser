using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SqlAnalyzer.Core.Formatting;
using SqlAnalyzer.Core.Infrastructure;
using SqlAnalyzer.Core.Models;
using SqlAnalyzer.Core.Parsing;
using SqlAnalyzer.Core.Resolution;

namespace SqlAnalyzer.Core.Orchestration
{
    /// <summary>
    /// Main entry point for the SQL analysis pipeline.
    ///
    /// Pipeline stages:
    ///   1. Read and split SQL scripts into GO-separated batches.
    ///   2. Extract object references from each batch via ScriptDom.
    ///   3. Resolve each reference against SQL Server metadata (Level 2).
    ///   4. Recursively resolve transitive dependencies (Level 3).
    ///   5. Format the retrieved definitions.
    ///   6. Return a fully populated <see cref="AnalysisResult"/>.
    /// </summary>
    public sealed class SqlAnalysisOrchestrator
    {
        private readonly AnalysisOptions _options;
        private readonly IConnectionFactory _connectionFactory;
        private readonly SqlScriptParser _scriptParser;
        private readonly SqlObjectResolver _resolver;
        private readonly DependencyResolver _dependencyResolver;
        private readonly SqlDefinitionFormatter _formatter;

        /// <summary>
        /// Creates the orchestrator from a fully populated <see cref="AnalysisOptions"/>.
        /// <see cref="AnalysisOptions.BaseConnectionString"/> and
        /// <see cref="AnalysisOptions.DefaultDatabase"/> must both be set.
        /// </summary>
        public SqlAnalysisOrchestrator(AnalysisOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            if (string.IsNullOrWhiteSpace(options.BaseConnectionString))
                throw new ArgumentException(
                    "BaseConnectionString is required when not providing an existing SqlConnection.",
                    nameof(options));
            if (string.IsNullOrWhiteSpace(options.DefaultDatabase))
                throw new ArgumentException("DefaultDatabase is required.", nameof(options));

            _connectionFactory = new ConnectionFactory(options);
            (_scriptParser, _resolver, _dependencyResolver, _formatter) = CreateComponents(_connectionFactory, _options);
        }

        /// <summary>
        /// Creates the orchestrator from an existing <see cref="SqlConnection"/> supplied by the
        /// parent application.  The connection is used as a template: its server address and
        /// authentication details are reused while the Initial Catalog is swapped dynamically so
        /// that multi-catalog object resolution keeps working correctly.
        ///
        /// The provided connection is NOT owned by the orchestrator and will NOT be closed.
        /// </summary>
        public SqlAnalysisOrchestrator(
            SqlConnection existingConnection,
            string defaultDatabase,
            AnalysisOptions? options = null)
        {
            if (existingConnection is null) throw new ArgumentNullException(nameof(existingConnection));
            if (string.IsNullOrWhiteSpace(defaultDatabase))
                throw new ArgumentException("defaultDatabase must not be empty.", nameof(defaultDatabase));

            _options = new AnalysisOptions
            {
                DefaultDatabase              = defaultDatabase,
                BaseConnectionString         = existingConnection.ConnectionString,
                MaxDependencyDepth           = options?.MaxDependencyDepth        ?? 5,
                ResolveRecursiveDependencies = options?.ResolveRecursiveDependencies ?? true,
                IncludeTables                = options?.IncludeTables             ?? false,
                ConnectionTimeoutSeconds     = options?.ConnectionTimeoutSeconds  ?? 30,
                CommandTimeoutSeconds        = options?.CommandTimeoutSeconds     ?? 60
            };

            _connectionFactory = new ConnectionFactory(existingConnection, _options.ConnectionTimeoutSeconds);
            (_scriptParser, _resolver, _dependencyResolver, _formatter) = CreateComponents(_connectionFactory, _options);
        }

        /// <summary>
        /// Allows injecting a custom <see cref="IConnectionFactory"/> (e.g. for unit testing).
        /// </summary>
        public SqlAnalysisOrchestrator(AnalysisOptions options, IConnectionFactory connectionFactory)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            (_scriptParser, _resolver, _dependencyResolver, _formatter) = CreateComponents(_connectionFactory, _options);
        }

        // ------------------------------------------------------------------ factory helper

        private static (SqlScriptParser, SqlObjectResolver, DependencyResolver, SqlDefinitionFormatter)
            CreateComponents(IConnectionFactory factory, AnalysisOptions opts)
        {
            var parser    = new SqlScriptParser();
            var resolver  = new SqlObjectResolver(factory, opts.CommandTimeoutSeconds);
            int depth     = opts.ResolveRecursiveDependencies ? opts.MaxDependencyDepth : 0;
            var depRes    = new DependencyResolver(resolver, depth, opts.IncludeTables);
            var formatter = new SqlDefinitionFormatter();
            return (parser, resolver, depRes, formatter);
        }

        // ------------------------------------------------------------------ public sync API

        /// <summary>
        /// Analyses one or more .sql files synchronously and returns a complete <see cref="AnalysisResult"/>.
        /// </summary>
        public AnalysisResult Analyse(IEnumerable<string> sqlFilePaths)
        {
            if (sqlFilePaths is null) throw new ArgumentNullException(nameof(sqlFilePaths));

            var result = new AnalysisResult();
            var allRawReferences = new List<SqlObjectReference>();

            // ---- Stage 1 & 2: parse scripts and extract references ----
            foreach (string path in sqlFilePaths)
            {
                if (!File.Exists(path))
                {
                    result.Diagnostics.Add(Diagnostic.Error($"File not found: {path}"));
                    continue;
                }

                try
                {
                    // BOM-aware read keeps content correct for OriginalScripts display.
                    string content = File.ReadAllText(path, Encoding.UTF8);
                    result.OriginalScripts.Add(new KeyValuePair<string, string>(path, content));

                    var batches = _scriptParser.ParseText(content, _options.DefaultDatabase);

                    foreach (var batch in batches)
                    {
                        foreach (string err in batch.ParseErrors)
                            result.Diagnostics.Add(Diagnostic.Warning($"Parse warning in {Path.GetFileName(path)} batch {batch.BatchIndex}: {err}"));

                        var refs = ObjectReferenceExtractor.Extract(batch, _options.DefaultDatabase);
                        allRawReferences.AddRange(refs);
                    }
                }
                catch (Exception ex)
                {
                    result.Diagnostics.Add(Diagnostic.Error($"Reading/parsing {path}: {ex.Message}"));
                }
            }

            // ---- Stage 3 & 4: resolve references and dependencies ----
            var visited     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var distinctRefs = DeduplicateReferences(allRawReferences);

            foreach (var reference in distinctRefs)
            {
                var node = _dependencyResolver.ResolveTree(reference, result, visited, depth: 0);
                if (node != null)
                    result.DependencyGraph.Add(node);
            }

            // ---- Stage 5: format all retrieved definitions ----
            FormatDefinitions(result);

            return result;
        }

        /// <summary>Convenience overload — analyses a single file.</summary>
        public AnalysisResult Analyse(string sqlFilePath) => Analyse(new[] { sqlFilePath });

        // ------------------------------------------------------------------ public async API

        /// <summary>
        /// Analyses one or more .sql files asynchronously, resolving objects in each database
        /// concurrently via <see cref="Task.WhenAll"/>.
        /// </summary>
        public async Task<AnalysisResult> AnalyseAsync(
            IEnumerable<string> sqlFilePaths,
            CancellationToken cancellationToken = default)
        {
            if (sqlFilePaths is null) throw new ArgumentNullException(nameof(sqlFilePaths));

            var result           = new AnalysisResult();
            var allRawReferences = new List<SqlObjectReference>();

            // ---- Stage 1 & 2: parse scripts (no DB I/O — always sync) ----
            foreach (string path in sqlFilePaths)
            {
                if (!File.Exists(path))
                {
                    result.Diagnostics.Add(Diagnostic.Error($"File not found: {path}"));
                    continue;
                }

                try
                {
                    string content = File.ReadAllText(path, Encoding.UTF8);
                    result.OriginalScripts.Add(new KeyValuePair<string, string>(path, content));

                    var batches = _scriptParser.ParseText(content, _options.DefaultDatabase);

                    foreach (var batch in batches)
                    {
                        foreach (string err in batch.ParseErrors)
                            result.Diagnostics.Add(Diagnostic.Warning($"Parse warning in {Path.GetFileName(path)} batch {batch.BatchIndex}: {err}"));

                        var refs = ObjectReferenceExtractor.Extract(batch, _options.DefaultDatabase);
                        allRawReferences.AddRange(refs);
                    }
                }
                catch (Exception ex)
                {
                    result.Diagnostics.Add(Diagnostic.Error($"Reading/parsing {path}: {ex.Message}"));
                }
            }

            // ---- Stage 3: resolve root objects per database concurrently ----
            var distinctRefs = DeduplicateReferences(allRawReferences);

            // Group by effective database so each group opens one connection batch.
            var groups = distinctRefs
                .GroupBy(r => r.EffectiveDatabase ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var groupTasks = groups.Select(g => ResolveGroupAsync(g.ToList(), cancellationToken));
            var groupResults = await Task.WhenAll(groupTasks);

            // Merge group results into the shared AnalysisResult sequentially (no lock needed).
            foreach (var (resolved, unresolved, diags) in groupResults)
            {
                foreach (var kv in resolved)
                    result.ResolvedObjects[kv.Key] = kv.Value;
                result.UnresolvedObjects.AddRange(unresolved);
                result.Diagnostics.AddRange(diags);
            }

            // ---- Stage 4: build dependency tree (uses cache from stage 3) ----
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var reference in distinctRefs)
            {
                var node = await _dependencyResolver.ResolveTreeAsync(reference, result, visited, 0, cancellationToken);
                if (node != null)
                    result.DependencyGraph.Add(node);
            }

            // ---- Stage 5: format all retrieved definitions ----
            FormatDefinitions(result);

            return result;
        }

        /// <summary>Convenience async overload — analyses a single file.</summary>
        public Task<AnalysisResult> AnalyseAsync(string sqlFilePath, CancellationToken cancellationToken = default)
            => AnalyseAsync(new[] { sqlFilePath }, cancellationToken);

        // ------------------------------------------------------------------ private helpers

        /// <summary>
        /// Resolves all references in one database group. Returns a tuple of (resolved, unresolved, diagnostics)
        /// so the caller can merge without locks.
        /// </summary>
        private async Task<(Dictionary<string, ResolvedSqlObject>, List<UnresolvedObject>, List<Diagnostic>)>
            ResolveGroupAsync(List<SqlObjectReference> refs, CancellationToken ct)
        {
            var resolved   = new Dictionary<string, ResolvedSqlObject>(StringComparer.OrdinalIgnoreCase);
            var unresolved = new List<UnresolvedObject>();
            var diags      = new List<Diagnostic>();

            foreach (var reference in refs)
            {
                ct.ThrowIfCancellationRequested();

                var obj = await _resolver.ResolveAsync(reference, diags, ct);
                if (obj == null)
                {
                    unresolved.Add(new UnresolvedObject
                    {
                        Reference = reference,
                        Reason    = "Object not found in SQL Server metadata."
                    });
                }
                else
                {
                    obj.DiscoveryDepth = 0;
                    resolved[reference.ResolutionKey] = obj;
                }
            }

            return (resolved, unresolved, diags);
        }

        private void FormatDefinitions(AnalysisResult result)
        {
            foreach (var resolved in result.ResolvedObjects.Values)
            {
                if (!string.IsNullOrWhiteSpace(resolved.RawDefinition))
                {
                    resolved.FormattedDefinition = _formatter.Format(
                        resolved.RawDefinition,
                        resolved.Reference.FullName,
                        result.Diagnostics);
                }
                else if (resolved.IsEncrypted)
                {
                    resolved.Notes = "Object is encrypted — definition unavailable.";
                }
                else if (resolved.DefinitionIsNull)
                {
                    resolved.Notes = "Definition is NULL in sys.sql_modules (may be a CLR or system object).";
                }
            }
        }

        private static List<SqlObjectReference> DeduplicateReferences(List<SqlObjectReference> refs)
        {
            var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unique = new List<SqlObjectReference>(refs.Count);

            foreach (var r in refs)
            {
                if (seen.Add(r.ResolutionKey))
                    unique.Add(r);
            }

            return unique;
        }
    }
}


