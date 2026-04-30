using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlAnalyzer.Core.Models;

namespace SqlAnalyzer.Core.Resolution
{
    /// <summary>
    /// Resolves dependencies recursively up to a configured depth.
    /// Tracks visited objects to prevent infinite recursion on circular dependencies.
    /// Populates a shared <see cref="AnalysisResult"/> with all discovered objects.
    /// </summary>
    public sealed class DependencyResolver
    {
        private readonly SqlObjectResolver _resolver;
        private readonly int _maxDepth;
        private readonly bool _includeTables;

        public DependencyResolver(SqlObjectResolver resolver, int maxDepth = 5, bool includeTables = false)
        {
            _resolver     = resolver ?? throw new ArgumentNullException(nameof(resolver));
            // maxDepth == 0 is valid: it means "resolve root objects but no transitive deps".
            _maxDepth     = maxDepth >= 0 ? maxDepth : 5;
            _includeTables = includeTables;
        }

        // ------------------------------------------------------------------ sync entry

        /// <summary>
        /// Resolves a root object reference and all its transitive dependencies.
        /// </summary>
        public DependencyNode? ResolveTree(
            SqlObjectReference reference,
            AnalysisResult result,
            HashSet<string> visited,
            int depth)
        {
            if (reference is null) throw new ArgumentNullException(nameof(reference));
            if (result   is null) throw new ArgumentNullException(nameof(result));
            if (visited  is null) throw new ArgumentNullException(nameof(visited));

            string key = reference.ResolutionKey;

            if (!visited.Add(key))
            {
                if (result.ResolvedObjects.TryGetValue(key, out var existing))
                    return new DependencyNode { Object = existing };
                return null;
            }

            if (depth > _maxDepth)
            {
                result.Diagnostics.Add(Diagnostic.Info($"Max depth {_maxDepth} reached at {reference.FullName} — not going deeper."));
                return null;
            }

            var resolved = _resolver.Resolve(reference, result.Diagnostics);
            if (resolved == null)
            {
                result.UnresolvedObjects.Add(new UnresolvedObject
                {
                    Reference = reference,
                    Reason    = "Object not found in SQL Server metadata."
                });
                return null;
            }

            if (!_includeTables && resolved.ObjectType == SqlObjectType.Table)
                return null;

            resolved.DiscoveryDepth = depth;
            result.ResolvedObjects[key] = resolved;

            var node = new DependencyNode { Object = resolved };

            if (!ShouldResolveChildren(resolved)) return node;

            var deps = _resolver.ResolveDependencies(resolved, result.Diagnostics);
            foreach (var dep in deps)
            {
                var childNode = ResolveTree(dep, result, visited, depth + 1);
                if (childNode != null)
                    node.Children.Add(childNode);
            }

            return node;
        }

        // ------------------------------------------------------------------ async entry

        /// <summary>
        /// Async version of <see cref="ResolveTree"/>.
        /// Reuses any object already present in <paramref name="result"/> to avoid
        /// redundant round-trips when roots were pre-resolved by <c>AnalyseAsync</c>.
        /// </summary>
        public async Task<DependencyNode?> ResolveTreeAsync(
            SqlObjectReference reference,
            AnalysisResult result,
            HashSet<string> visited,
            int depth,
            CancellationToken cancellationToken = default)
        {
            if (reference is null) throw new ArgumentNullException(nameof(reference));
            if (result   is null) throw new ArgumentNullException(nameof(result));
            if (visited  is null) throw new ArgumentNullException(nameof(visited));

            string key = reference.ResolutionKey;

            if (!visited.Add(key))
            {
                if (result.ResolvedObjects.TryGetValue(key, out var existing))
                    return new DependencyNode { Object = existing };
                return null;
            }

            if (depth > _maxDepth)
            {
                result.Diagnostics.Add(Diagnostic.Info($"Max depth {_maxDepth} reached at {reference.FullName} — not going deeper."));
                return null;
            }

            // Use cached result when available (e.g. pre-resolved by AnalyseAsync group phase).
            ResolvedSqlObject? resolved;
            if (result.ResolvedObjects.TryGetValue(key, out var cached))
            {
                resolved = cached;
            }
            else
            {
                resolved = await _resolver.ResolveAsync(reference, result.Diagnostics, cancellationToken);
                if (resolved == null)
                {
                    result.UnresolvedObjects.Add(new UnresolvedObject
                    {
                        Reference = reference,
                        Reason    = "Object not found in SQL Server metadata."
                    });
                    return null;
                }

                if (!_includeTables && resolved.ObjectType == SqlObjectType.Table)
                    return null;

                resolved.DiscoveryDepth = depth;
                result.ResolvedObjects[key] = resolved;
            }

            var node = new DependencyNode { Object = resolved };

            if (!ShouldResolveChildren(resolved)) return node;

            var deps = await _resolver.ResolveDependenciesAsync(resolved, result.Diagnostics, cancellationToken);
            foreach (var dep in deps)
            {
                var childNode = await ResolveTreeAsync(dep, result, visited, depth + 1, cancellationToken);
                if (childNode != null)
                    node.Children.Add(childNode);
            }

            return node;
        }

        // ------------------------------------------------------------------ helpers

        private static bool ShouldResolveChildren(ResolvedSqlObject obj)
        {
            return obj.ObjectType == SqlObjectType.View
                || obj.ObjectType == SqlObjectType.StoredProcedure
                || obj.ObjectType == SqlObjectType.ScalarFunction
                || obj.ObjectType == SqlObjectType.TableValuedFunction
                || obj.ObjectType == SqlObjectType.InlineTableValuedFunction;
        }
    }
}

