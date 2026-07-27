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
                        Description = $"Komponen '{comp.Name}' bertipe '{comp.Type}' merupakan Blocking Transformation. Komponen ini menahan seluruh baris data di dalam RAM sebelum dapat melanjutkan ke pipeline berikutnya, menyebabkan kebocoran memori (memory spooling) dan menurunkan throughput secara signifikan.",
                        Remediation = "Hapus komponen SSIS Sort/Aggregate. Sebagai gantinya, tambahkan klausa SQL 'ORDER BY' atau 'GROUP BY' langsung pada query OLE DB Source database, lalu set properti 'IsSorted = True' pada output source.",
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
                        Description = $"Komponen '{comp.Name}' menggunakan wildcard 'SELECT *'. Hal ini menarik seluruh kolom dari database yang tidak diperlukan, menambah beban memori buffer SSIS, serta berisiko memecahkan paket jika skema tabel sumber berubah.",
                        Remediation = "Sebutkan nama-nama kolom yang dibutuhkan secara eksplisit dalam query SQL (contoh: SELECT CustomerId, FullName, Email FROM ...).",
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
                        Description = $"Ditemukan kredensial/kata sandi yang ditulis secara mentah (hardcoded) pada komponen '{comp.Name}'. Hal ini melanggar standar keamanan ISO 27001 / SOC2 dan menyulitkan deployment lintas environment (Dev/Staging/Prod).",
                        Remediation = "Gunakan SSIS Project Parameters atau SSISDB Environment Reference untuk menginjeksikan kata sandi secara dinamis dari vault aman.",
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
                            Description = $"Komponen '{comp.Name}' memiliki {kvp.Value} cabang aliran data (fan-out) sekaligus. Hal ini memicu fragmentasi memori buffer dan persaingan thread pada engine SSIS Data Flow.",
                            Remediation = "Pertimbangkan untuk menyederhanakan alur data dengan membaginya ke dalam sub-task terpisah atau menggunakan tabel staging sementara sebelum pemecahan cabang alur.",
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
                        Description = $"Komponen '{comp.Name}' terdaftar di dalam paket namun tidak memiliki koneksi aliran data (orphan). Komponen ini berpotensi menjadi 'dead code' yang mengotori paket.",
                        Remediation = "Periksa kembali paket SSIS Anda. Jika komponen ini tidak lagi digunakan, hapus dari canvas untuk menjaga kebersihan paket.",
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
