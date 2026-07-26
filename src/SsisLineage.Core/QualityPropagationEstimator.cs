using System;
using System.Collections.Generic;
using System.Linq;
using SsisLineage.Core.Models;

namespace SsisLineage.Core
{
    // ── Quality Models ────────────────────────────────────────────────────────

    public enum QualitySeverity { Warning, Error, Critical }

    public sealed class QualityTag
    {
        public string EntityName { get; set; } = ""; // Table or Table.Column
        public string IssueDescription { get; set; } = "";
        public QualitySeverity Severity { get; set; } = QualitySeverity.Error;
        public string TaggedBy { get; set; } = "Data Engineering Team";
        public DateTime TaggedDate { get; set; } = DateTime.Now;
    }

    public sealed class PropagationNode
    {
        public string EntityName { get; set; } = "";
        public string EntityType { get; set; } = ""; // "Table", "Column", "Procedure"
        public int HopsFromSource { get; set; }
        public QualitySeverity InheritedSeverity { get; set; }
        public string ViaPackage { get; set; } = "";
        public string ViaTask { get; set; } = "";
    }

    public sealed class QualityPropagationReport
    {
        public QualityTag SourceTag { get; set; } = new();
        public List<PropagationNode> ImpactedNodes { get; set; } = new();
        public int DirectImpactCount => ImpactedNodes.Count(n => n.HopsFromSource == 1);
        public int TotalImpactCount => ImpactedNodes.Count;
        public int MaxHops => ImpactedNodes.Count == 0 ? 0 : ImpactedNodes.Max(n => n.HopsFromSource);
        public double ContaminationRate { get; set; }
        public DateTime GeneratedDate { get; set; } = DateTime.Now;
    }

    // ── Engine ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Traces data quality issue contamination paths forward through the LineageGraph.
    /// Uses Forward BFS traversal to calculate downstream impact.
    /// 100% offline in-memory graph analysis.
    /// </summary>
    public static class QualityPropagationEstimator
    {
        public static QualityPropagationReport Estimate(QualityTag tag, LineageGraph graph)
        {
            if (tag == null || string.IsNullOrWhiteSpace(tag.EntityName) || graph == null || graph.ColumnMappings.Count == 0)
            {
                return new QualityPropagationReport
                {
                    SourceTag = tag ?? new QualityTag(),
                    ImpactedNodes = new List<PropagationNode>()
                };
            }

            var targetEntity = tag.EntityName.Trim();
            var isColumn = targetEntity.Contains('.');

            var impacted = new List<PropagationNode>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { targetEntity };

            var queue = new Queue<(string Entity, int Hops)>();
            queue.Enqueue((targetEntity, 0));

            // Calculate total unique entities in graph for rate calculation
            var totalGraphEntities = graph.ColumnMappings
                .SelectMany(m => new[]
                {
                    FormatColumn(m.SourceSchema, m.SourceTable, m.SourceColumnName),
                    FormatColumn(m.TargetSchema, m.TargetTable, m.TargetColumnName)
                })
                .Where(e => !string.IsNullOrEmpty(e))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            while (queue.Count > 0)
            {
                var (current, hops) = queue.Dequeue();

                // Find downstream mappings where 'current' acts as a source
                var downstreamMappings = graph.ColumnMappings
                    .Where(m => MatchesSource(m, current))
                    .ToList();

                foreach (var m in downstreamMappings)
                {
                    var tgtCol = FormatColumn(m.TargetSchema, m.TargetTable, m.TargetColumnName);
                    var tgtTbl = FormatTable(m.TargetSchema, m.TargetTable);

                    var nextEntity = isColumn ? tgtCol : tgtTbl;
                    if (string.IsNullOrEmpty(nextEntity)) continue;

                    if (!visited.Contains(nextEntity))
                    {
                        visited.Add(nextEntity);

                        var pkgName = graph.Packages.FirstOrDefault(p => p.Id == m.PackageId)?.Name ?? m.PackageId;
                        var taskName = graph.Tasks.FirstOrDefault(t => t.Id == m.TaskId)?.Name ?? m.TaskId;

                        impacted.Add(new PropagationNode
                        {
                            EntityName = nextEntity,
                            EntityType = isColumn ? "Column" : "Table",
                            HopsFromSource = hops + 1,
                            InheritedSeverity = CalculateInheritedSeverity(tag.Severity, hops + 1),
                            ViaPackage = pkgName,
                            ViaTask = taskName
                        });

                        queue.Enqueue((nextEntity, hops + 1));
                    }
                }
            }

            double rate = totalGraphEntities == 0 ? 0 : (double)impacted.Count / totalGraphEntities * 100.0;

            return new QualityPropagationReport
            {
                SourceTag = tag,
                ImpactedNodes = impacted.OrderBy(n => n.HopsFromSource).ThenBy(n => n.EntityName).ToList(),
                ContaminationRate = Math.Min(100.0, Math.Round(rate, 1))
            };
        }

        private static bool MatchesSource(ColumnMap m, string entity)
        {
            if (string.IsNullOrEmpty(entity)) return false;
            var srcCol = FormatColumn(m.SourceSchema, m.SourceTable, m.SourceColumnName);
            var srcTbl = FormatTable(m.SourceSchema, m.SourceTable);

            if (entity.Equals(srcCol, StringComparison.OrdinalIgnoreCase)) return true;
            if (entity.Equals(srcTbl, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(m.SourceTable) && m.SourceTable.Equals(entity, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(m.SourceColumnName) && m.SourceColumnName.Equals(entity, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static QualitySeverity CalculateInheritedSeverity(QualitySeverity source, int hops)
        {
            if (hops >= 3 && source == QualitySeverity.Critical) return QualitySeverity.Error;
            if (hops >= 3 && source == QualitySeverity.Error) return QualitySeverity.Warning;
            return source;
        }

        private static string FormatColumn(string schema, string table, string column)
        {
            var t = FormatTable(schema, table);
            return string.IsNullOrEmpty(column) ? t : $"{t}.{column}";
        }

        private static string FormatTable(string schema, string table) =>
            string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";

        // ── Markdown Report Generator ────────────────────────────────────────

        public static string GenerateMarkdown(QualityPropagationReport report)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Data Quality Propagation & Contamination Report");
            sb.AppendLine();
            sb.AppendLine($"> Generated: {report.GeneratedDate:yyyy-MM-dd HH:mm}");
            sb.AppendLine();
            sb.AppendLine("## 🔴 Source Quality Issue");
            sb.AppendLine();
            sb.AppendLine($"| Property | Value |");
            sb.AppendLine($"|----------|-------|");
            sb.AppendLine($"| **Source Entity** | `{report.SourceTag.EntityName}` |");
            sb.AppendLine($"| **Issue Description** | {report.SourceTag.IssueDescription} |");
            sb.AppendLine($"| **Severity** | {report.SourceTag.Severity} |");
            sb.AppendLine($"| **Tagged By** | {report.SourceTag.TaggedBy} |");
            sb.AppendLine();
            sb.AppendLine("## 📊 Propagation Metrics");
            sb.AppendLine();
            sb.AppendLine($"- **Directly Impacted Entities (1-hop):** {report.DirectImpactCount}");
            sb.AppendLine($"- **Total Downstream Contaminated Entities:** {report.TotalImpactCount}");
            sb.AppendLine($"- **Max Contamination Depth (Hops):** {report.MaxHops}");
            sb.AppendLine($"- **Pipeline Contamination Rate:** {report.ContaminationRate}%");
            sb.AppendLine();
            sb.AppendLine("## ☣️ Contamination Propagation Path");
            sb.AppendLine();
            sb.AppendLine("| Hops | Entity | Type | Inherited Severity | Via Package | Task |");
            sb.AppendLine("|------|--------|------|--------------------|-------------|------|");

            foreach (var n in report.ImpactedNodes)
            {
                var sevBadge = n.InheritedSeverity switch
                {
                    QualitySeverity.Critical => "🔴 Critical",
                    QualitySeverity.Error    => "🟠 Error",
                    _                        => "🟡 Warning"
                };
                sb.AppendLine($"| {n.HopsFromSource} | `{n.EntityName}` | {n.EntityType} | {sevBadge} | {n.ViaPackage} | {n.ViaTask} |");
            }

            return sb.ToString();
        }
    }
}
