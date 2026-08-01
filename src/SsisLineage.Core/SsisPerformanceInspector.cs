using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SsisLineage.Core.Models;

namespace SsisLineage.Core
{
    public enum InspectionSeverity
    {
        Critical,
        Warning,
        Info
    }

    public enum InspectionCategory
    {
        Performance,
        Security,
        Architecture,
        Maintainability
    }

    public class InspectionFinding
    {
        public string RuleId { get; set; } = "";
        public string RuleTitle { get; set; } = "";
        public InspectionSeverity Severity { get; set; }
        public InspectionCategory Category { get; set; }
        public string PackageId { get; set; } = "";
        public string PackageName { get; set; } = "";
        public string TaskOrComponentId { get; set; } = "";
        public string TaskOrComponentName { get; set; } = "";
        public string Description { get; set; } = "";
        public string Remediation { get; set; } = "";
        public string Snippet { get; set; } = "";
    }

    public class ProjectInspectionReport
    {
        public List<InspectionFinding> Findings { get; set; } = new();
        public int TotalPackages { get; set; }
        public int TotalTasks { get; set; }
        public int TotalComponents { get; set; }
        public int HealthScore { get; set; } = 100;
        public string HealthGrade { get; set; } = "A+";

        public int CriticalCount => Findings.Count(f => f.Severity == InspectionSeverity.Critical);
        public int WarningCount => Findings.Count(f => f.Severity == InspectionSeverity.Warning);
        public int InfoCount => Findings.Count(f => f.Severity == InspectionSeverity.Info);
    }

    public static class SsisPerformanceInspector
    {
        public static ProjectInspectionReport InspectProject(LineageGraph graph)
        {
            var report = new ProjectInspectionReport
            {
                TotalPackages = graph.Packages.Count,
                TotalTasks = graph.Tasks.Count,
                TotalComponents = graph.Components.Count
            };

            if (graph == null) return report;

            string GetPackageName(string pkgId)
            {
                var p = graph.Packages.FirstOrDefault(x => x.Id == pkgId);
                return p?.Name ?? pkgId;
            }

            string GetTaskName(string taskId)
            {
                var t = graph.Tasks.FirstOrDefault(x => x.Id == taskId);
                return t?.Name ?? taskId;
            }

            // ── Rule 1: PERF-001 Blocking Transformations (Sort / Aggregate) ──
            foreach (var comp in graph.Components)
            {
                var type = comp.Type ?? "";
                if (type.Equals("Sort", StringComparison.OrdinalIgnoreCase) ||
                    type.Equals("Aggregate", StringComparison.OrdinalIgnoreCase) ||
                    type.Contains("Sort", StringComparison.OrdinalIgnoreCase) ||
                    type.Contains("Aggregate", StringComparison.OrdinalIgnoreCase))
                {
                    var taskName = GetTaskName(comp.TaskId);
                    var pkgName = GetPackageName(comp.PackageId);

                    report.Findings.Add(new InspectionFinding
                    {
                        RuleId = "PERF-001",
                        RuleTitle = "Blocking Transformation Component",
                        Severity = InspectionSeverity.Critical,
                        Category = InspectionCategory.Performance,
                        PackageId = comp.PackageId,
                        PackageName = pkgName,
                        TaskOrComponentId = comp.Id,
                        TaskOrComponentName = comp.Name,
                        Description = $"Component '{comp.Name}' of type '{comp.Type}' is a Blocking Transformation. It buffers all data rows in RAM before sending them to the downstream pipeline, causing memory spooling and significantly reducing throughput.",
                        Remediation = "Remove the SSIS Sort/Aggregate component. Instead, push the sorting/aggregation to the database layer using 'ORDER BY' or 'GROUP BY' in the OLE DB Source query, and set 'IsSorted = True' on the output.",
                        Snippet = $"Type: {comp.Type} | Task: {taskName}"
                    });
                }
            }

            // ── Rule 2: PERF-002 SELECT * Wildcard Query ──
            foreach (var comp in graph.Components)
            {
                var sql = comp.SqlQueryOrTable ?? "";
                if (Regex.IsMatch(sql, @"(?i)\bSELECT\s+\*\b"))
                {
                    var taskName = GetTaskName(comp.TaskId);
                    var pkgName = GetPackageName(comp.PackageId);

                    report.Findings.Add(new InspectionFinding
                    {
                        RuleId = "PERF-002",
                        RuleTitle = "Unoptimized SELECT * Query",
                        Severity = InspectionSeverity.Warning,
                        Category = InspectionCategory.Performance,
                        PackageId = comp.PackageId,
                        PackageName = pkgName,
                        TaskOrComponentId = comp.Id,
                        TaskOrComponentName = comp.Name,
                        Description = $"Component '{comp.Name}' uses a 'SELECT *' wildcard. This extracts all columns from the database unnecessarily, increasing SSIS memory buffer pressure and risking package failure if the source schema changes.",
                        Remediation = "Explicitly list the required column names in the SQL query (e.g., SELECT CustomerId, FullName, Email FROM ...).",
                        Snippet = sql.Length > 120 ? sql.Substring(0, 120) + "..." : sql
                    });
                }
            }

            // ── Rule 3: SEC-001 Hardcoded Passwords & Credentials ──
            foreach (var comp in graph.Components)
            {
                var sql = comp.SqlQueryOrTable ?? "";
                var conn = comp.ConnectionManager ?? "";
                var combined = sql + " " + conn;

                if (Regex.IsMatch(combined, @"(?i)\b(Password|Pwd)\s*=\s*['""]?[^;\s'""]+"))
                {
                    var pkgName = GetPackageName(comp.PackageId);

                    report.Findings.Add(new InspectionFinding
                    {
                        RuleId = "SEC-001",
                        RuleTitle = "Hardcoded Security Credentials",
                        Severity = InspectionSeverity.Critical,
                        Category = InspectionCategory.Security,
                        PackageId = comp.PackageId,
                        PackageName = pkgName,
                        TaskOrComponentId = comp.Id,
                        TaskOrComponentName = comp.Name,
                        Description = $"Found hardcoded credentials/passwords in component '{comp.Name}'. This violates ISO 27001 / SOC2 security standards and complicates cross-environment deployments (Dev/Staging/Prod).",
                        Remediation = "Use SSIS Project Parameters or SSISDB Environment References to inject passwords dynamically from a secure vault.",
                        Snippet = Regex.Replace(combined, @"(?i)(Password|Pwd)\s*=\s*[^;\s]+", "$1=********")
                    });
                }
            }

            // ── Rule 4: ARCH-001 Excessive Multi-Path Fan-Out ──
            var outgoingEdgeCounts = graph.DataFlowEdges
                .GroupBy(e => e.FromComponentId)
                .ToDictionary(g => g.Key, g => g.Count());

            foreach (var kvp in outgoingEdgeCounts)
            {
                if (kvp.Value >= 4)
                {
                    var comp = graph.Components.FirstOrDefault(c => c.Id == kvp.Key);
                    if (comp != null)
                    {
                        var pkgName = GetPackageName(comp.PackageId);
                        report.Findings.Add(new InspectionFinding
                        {
                            RuleId = "ARCH-001",
                            RuleTitle = "Excessive Data Flow Fan-Out",
                            Severity = InspectionSeverity.Warning,
                            Category = InspectionCategory.Architecture,
                            PackageId = comp.PackageId,
                            PackageName = pkgName,
                            TaskOrComponentId = comp.Id,
                            TaskOrComponentName = comp.Name,
                            Description = $"Component '{comp.Name}' has {kvp.Value} simultaneous outgoing data flow paths (fan-out). This triggers memory buffer fragmentation and thread contention in the SSIS Data Flow engine.",
                            Remediation = "Consider simplifying the data flow by splitting it into separate sub-tasks or using temporary staging tables before splitting the paths.",
                            Snippet = $"Outgoing Data Paths: {kvp.Value} branches"
                        });
                    }
                }
            }

            // ── Rule 5: MAINT-001 Unmapped / Orphan Component ──
            var mappedCompIds = new HashSet<string>(
                graph.DataFlowEdges.Select(e => e.FromComponentId)
                .Concat(graph.DataFlowEdges.Select(e => e.ToComponentId))
                .Concat(graph.ColumnMappings.Select(m => m.SourceComponentId))
                .Concat(graph.ColumnMappings.Select(m => m.TargetComponentId))
            );

            foreach (var comp in graph.Components)
            {
                if (!mappedCompIds.Contains(comp.Id))
                {
                    var pkgName = GetPackageName(comp.PackageId);
                    report.Findings.Add(new InspectionFinding
                    {
                        RuleId = "MAINT-001",
                        RuleTitle = "Orphan / Unmapped Component",
                        Severity = InspectionSeverity.Info,
                        Category = InspectionCategory.Maintainability,
                        PackageId = comp.PackageId,
                        PackageName = pkgName,
                        TaskOrComponentId = comp.Id,
                        TaskOrComponentName = comp.Name,
                        Description = $"Component '{comp.Name}' is registered in the package but has no data flow connections (orphan). This component could be 'dead code' cluttering the package.",
                        Remediation = "Review your SSIS package. If this component is no longer used, remove it from the canvas to keep the package clean.",
                        Snippet = $"Type: {comp.Type}"
                    });
                }
            }

            // ── Calculate Health Score & Grade ──
            int score = 100;
            score -= report.CriticalCount * 15;
            score -= report.WarningCount * 5;
            score -= report.InfoCount * 1;

            report.HealthScore = Math.Max(0, score);

            if (report.HealthScore >= 95) report.HealthGrade = "A+";
            else if (report.HealthScore >= 85) report.HealthGrade = "A";
            else if (report.HealthScore >= 75) report.HealthGrade = "B";
            else if (report.HealthScore >= 60) report.HealthGrade = "C";
            else if (report.HealthScore >= 40) report.HealthGrade = "D";
            else report.HealthGrade = "F";

            return report;
        }
    }
}
