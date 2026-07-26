using System;
using System.Collections.Generic;
using System.Linq;
using SsisLineage.Core.Models;

namespace SsisLineage.Core
{
    // ── Risk models ───────────────────────────────────────────────────────────

    public enum RiskLevel { Critical, High, Medium, Low }

    public sealed class TableRiskScore
    {
        /// <summary>Qualified table name, e.g. "dbo.MasterPinjaman".</summary>
        public string TableName { get; set; } = "";

        /// <summary>0–100 composite risk score.</summary>
        public int Score { get; set; }

        public RiskLevel Level => Score switch
        {
            >= 75 => RiskLevel.Critical,
            >= 50 => RiskLevel.High,
            >= 25 => RiskLevel.Medium,
            _     => RiskLevel.Low
        };

        /// <summary>Number of unique downstream tables that read from this table.</summary>
        public int DownstreamTableCount { get; set; }

        /// <summary>Number of packages that write to this table.</summary>
        public int WriterPackageCount { get; set; }

        /// <summary>Number of packages that read from this table.</summary>
        public int ReaderPackageCount { get; set; }

        /// <summary>Max transformation depth (hops from an ultimate source).</summary>
        public int Depth { get; set; }

        /// <summary>Number of direct column mappings referencing this table.</summary>
        public int MappingCount { get; set; }

        /// <summary>True if the table appears as a source for multi-package consumption.</summary>
        public bool IsShared => ReaderPackageCount > 1;

        /// <summary>True if this table is a final target (no downstream consumers).</summary>
        public bool IsFinalTarget { get; set; }

        // Score breakdown (0–25 each)
        public int FanOutScore { get; set; }
        public int DepthScore { get; set; }
        public int SharedScore { get; set; }
        public int MappingScore { get; set; }
    }

    public sealed class RiskReport
    {
        public IReadOnlyList<TableRiskScore> Scores { get; set; } = Array.Empty<TableRiskScore>();
        public int CriticalCount => Scores.Count(s => s.Level == RiskLevel.Critical);
        public int HighCount     => Scores.Count(s => s.Level == RiskLevel.High);
        public int MediumCount   => Scores.Count(s => s.Level == RiskLevel.Medium);
        public int LowCount      => Scores.Count(s => s.Level == RiskLevel.Low);
        public double AverageScore => Scores.Count == 0 ? 0 : Scores.Average(s => s.Score);
        public int TableCount => Scores.Count;
    }

    // ── Engine ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scores every table in a <see cref="LineageGraph"/> on a 0–100 risk scale.
    ///
    /// Score components (25 pts each):
    ///   1. Fan-out score  — how many unique downstream tables depend on it
    ///   2. Depth score    — how deep in the transformation chain it sits
    ///   3. Shared score   — how many distinct packages consume it
    ///   4. Mapping score  — raw number of column mappings referencing it
    ///
    /// Higher score = more dangerous to change.
    /// All analysis is in-memory graph traversal — 100% offline.
    /// </summary>
    public static class RiskScorer
    {
        public static RiskReport Score(LineageGraph graph)
        {
            if (graph == null || graph.ColumnMappings.Count == 0)
                return new RiskReport { Scores = Array.Empty<TableRiskScore>() };

            // ── Step 1: collect all unique tables ──────────────────────────
            var allTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in graph.ColumnMappings)
            {
                if (!string.IsNullOrEmpty(m.SourceTable)) allTables.Add(QualifiedName(m.SourceSchema, m.SourceTable));
                if (!string.IsNullOrEmpty(m.TargetTable)) allTables.Add(QualifiedName(m.TargetSchema, m.TargetTable));
            }

            if (allTables.Count == 0)
                return new RiskReport { Scores = Array.Empty<TableRiskScore>() };

            // ── Step 2: build table-level adjacency (source → target tables) ─
            // downstream[table] = set of downstream tables it feeds into
            var downstream = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            // readers[table] = set of packageIds that read from it
            var readers    = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            // writers[table] = set of packageIds that write to it
            var writers    = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            // mappings count
            var mappings   = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var t in allTables)
            {
                downstream[t] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                readers[t]    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                writers[t]    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                mappings[t]   = 0;
            }

            foreach (var m in graph.ColumnMappings)
            {
                var src = string.IsNullOrEmpty(m.SourceTable) ? null : QualifiedName(m.SourceSchema, m.SourceTable);
                var tgt = string.IsNullOrEmpty(m.TargetTable) ? null : QualifiedName(m.TargetSchema, m.TargetTable);

                if (src != null)
                {
                    if (tgt != null && !src.Equals(tgt, StringComparison.OrdinalIgnoreCase))
                        downstream[src].Add(tgt);
                    if (!string.IsNullOrEmpty(m.PackageId)) readers[src].Add(m.PackageId);
                    mappings[src]++;
                }
                if (tgt != null)
                {
                    if (!string.IsNullOrEmpty(m.PackageId)) writers[tgt].Add(m.PackageId);
                    mappings[tgt]++;
                }
            }

            // ── Step 3: BFS to compute max depth from ultimate sources ─────
            // Ultimate sources = tables with no upstream (don't appear as a target)
            var targets = new HashSet<string>(
                graph.ColumnMappings
                    .Where(m => !string.IsNullOrEmpty(m.TargetTable))
                    .Select(m => QualifiedName(m.TargetSchema, m.TargetTable)),
                StringComparer.OrdinalIgnoreCase);

            var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in allTables) depth[t] = 0;

            // Longest-path BFS (Bellman-Ford style, capped at table count)
            var iterCount = allTables.Count;
            for (var iter = 0; iter < iterCount; iter++)
            {
                var changed = false;
                foreach (var m in graph.ColumnMappings)
                {
                    var src = string.IsNullOrEmpty(m.SourceTable) ? null : QualifiedName(m.SourceSchema, m.SourceTable);
                    var tgt = string.IsNullOrEmpty(m.TargetTable) ? null : QualifiedName(m.TargetSchema, m.TargetTable);
                    if (src == null || tgt == null) continue;
                    if (depth[tgt] < depth[src] + 1)
                    {
                        depth[tgt] = depth[src] + 1;
                        changed = true;
                    }
                }
                if (!changed) break;
            }

            // ── Step 4: find final targets (no downstream tables) ──────────
            var sources = new HashSet<string>(
                graph.ColumnMappings
                    .Where(m => !string.IsNullOrEmpty(m.SourceTable))
                    .Select(m => QualifiedName(m.SourceSchema, m.SourceTable)),
                StringComparer.OrdinalIgnoreCase);

            // ── Step 5: normalise and compute scores ───────────────────────
            int maxFanOut  = allTables.Max(t => downstream[t].Count);
            int maxDepth   = allTables.Max(t => depth[t]);
            int maxReaders = allTables.Max(t => readers[t].Count);
            int maxMaps    = allTables.Max(t => mappings[t]);

            var scores = allTables.Select(t =>
            {
                var fanOutScore  = Scale(downstream[t].Count, maxFanOut,  25);
                var depthScore   = Scale(depth[t],            maxDepth,   25);
                var sharedScore  = Scale(readers[t].Count,   maxReaders, 25);
                var mappingScore = Scale(mappings[t],         maxMaps,    25);
                var total        = fanOutScore + depthScore + sharedScore + mappingScore;

                return new TableRiskScore
                {
                    TableName          = t,
                    Score              = total,
                    DownstreamTableCount = downstream[t].Count,
                    WriterPackageCount   = writers[t].Count,
                    ReaderPackageCount   = readers[t].Count,
                    Depth                = depth[t],
                    MappingCount         = mappings[t],
                    IsFinalTarget        = !sources.Contains(t) && targets.Contains(t),
                    FanOutScore          = fanOutScore,
                    DepthScore           = depthScore,
                    SharedScore          = sharedScore,
                    MappingScore         = mappingScore,
                };
            })
            .OrderByDescending(s => s.Score)
            .ToList();

            return new RiskReport { Scores = scores };
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static int Scale(int value, int max, int maxScore)
        {
            if (max <= 0 || value <= 0) return 0;
            return (int)Math.Round((double)value / max * maxScore);
        }

        private static string QualifiedName(string schema, string table) =>
            string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";

        // ── Markdown export ───────────────────────────────────────────────────

        public static string GenerateMarkdown(RiskReport report)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# SSIS Lineage Risk Report");
            sb.AppendLine();
            sb.AppendLine($"> Generated: {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine();
            sb.AppendLine("## Summary");
            sb.AppendLine();
            sb.AppendLine($"| Level | Count |");
            sb.AppendLine($"|-------|-------|");
            sb.AppendLine($"| 🔴 Critical (≥75) | {report.CriticalCount} |");
            sb.AppendLine($"| 🟠 High (50–74)   | {report.HighCount}     |");
            sb.AppendLine($"| 🟡 Medium (25–49) | {report.MediumCount}   |");
            sb.AppendLine($"| 🟢 Low (0–24)     | {report.LowCount}      |");
            sb.AppendLine($"| Average score     | {report.AverageScore:F1} |");
            sb.AppendLine();
            sb.AppendLine("## Risk Heatmap");
            sb.AppendLine();
            sb.AppendLine("| Table | Score | Level | Fan-Out | Depth | Shared By | Mappings |");
            sb.AppendLine("|-------|-------|-------|---------|-------|-----------|---------|");
            foreach (var s in report.Scores)
            {
                var lvl = s.Level switch
                {
                    RiskLevel.Critical => "🔴 Critical",
                    RiskLevel.High     => "🟠 High",
                    RiskLevel.Medium   => "🟡 Medium",
                    _                  => "🟢 Low"
                };
                sb.AppendLine($"| {s.TableName} | {s.Score} | {lvl} | {s.DownstreamTableCount} | {s.Depth} | {s.ReaderPackageCount} pkg(s) | {s.MappingCount} |");
            }
            return sb.ToString();
        }
    }
}
