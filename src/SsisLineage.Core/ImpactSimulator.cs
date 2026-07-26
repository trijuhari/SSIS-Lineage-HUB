using System;
using System.Collections.Generic;
using System.Linq;
using SsisLineage.Core.Models;

namespace SsisLineage.Core
{
    // ── Input models ─────────────────────────────────────────────────────────────

    /// <summary>The type of schema mutation to simulate.</summary>
    public enum SimulationChangeType
    {
        RenameColumn,
        DropColumn,
        RenameTable,
        DropTable,
        ChangeColumnType
    }

    /// <summary>A single proposed schema change fed into the What-If Simulator.</summary>
    public class SchemaChange
    {
        /// <summary>The change operation to simulate.</summary>
        public SimulationChangeType ChangeType { get; set; }

        /// <summary>Fully-qualified table, e.g. "dbo.MasterPinjaman" (schema.table or just table).</summary>
        public string Table { get; set; } = "";

        /// <summary>Column name being changed (not required for table-level changes).</summary>
        public string Column { get; set; } = "";

        /// <summary>New name (for rename operations).</summary>
        public string NewName { get; set; } = "";

        /// <summary>New type description string (for ChangeColumnType only).</summary>
        public string NewType { get; set; } = "";
    }

    // ── Result models ─────────────────────────────────────────────────────────────

    public enum ImpactSeverity { Critical, Warning, Info }

    /// <summary>A single impact finding produced by the simulator.</summary>
    public class ImpactFinding
    {
        public ImpactSeverity Severity { get; set; }
        public string Category { get; set; } = "";   // "Column Mapping", "Package", "Stored Procedure", "Task"
        public string Location { get; set; } = "";   // e.g. "Narik_Master_Pinjaman.dtsx / Load Data Flow"
        public string Description { get; set; } = "";
        public string AffectedObject { get; set; } = ""; // package name, proc name, etc.
    }

    /// <summary>A migration checklist item generated from impact findings.</summary>
    public class ChecklistItem
    {
        public string Action { get; set; } = "";
        public string Target { get; set; } = "";
        public string Reason { get; set; } = "";
        public bool IsRequired { get; set; } = true;
    }

    /// <summary>Full result of a What-If simulation run.</summary>
    public class SimulationResult
    {
        public IReadOnlyList<SchemaChange> Changes { get; }
        public IReadOnlyList<ImpactFinding> Findings { get; }
        public IReadOnlyList<ChecklistItem> Checklist { get; }

        // Summary counters
        public int AffectedPackages { get; }
        public int AffectedMappings { get; }
        public int AffectedProcedures { get; }
        public int AffectedTasks { get; }
        public bool HasCritical => Findings.Any(f => f.Severity == ImpactSeverity.Critical);

        public SimulationResult(
            IReadOnlyList<SchemaChange> changes,
            IReadOnlyList<ImpactFinding> findings,
            IReadOnlyList<ChecklistItem> checklist)
        {
            Changes = changes;
            Findings = findings;
            Checklist = checklist;
            AffectedPackages = findings.Select(f => f.AffectedObject).Where(o => o.EndsWith(".dtsx", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            AffectedMappings = findings.Count(f => f.Category == "Column Mapping");
            AffectedProcedures = findings.Count(f => f.Category == "Stored Procedure");
            AffectedTasks = findings.Count(f => f.Category == "Task");
        }
    }

    // ── Engine ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Simulates the impact of proposed schema changes (rename/drop column or table,
    /// change column type) against an existing <see cref="LineageGraph"/> without
    /// touching any real database.  All analysis is performed as an in-memory
    /// graph traversal — 100% offline.
    /// </summary>
    public static class ImpactSimulator
    {
        /// <summary>
        /// Run the simulation and return a full <see cref="SimulationResult"/>.
        /// </summary>
        public static SimulationResult Simulate(LineageGraph graph, IReadOnlyList<SchemaChange> changes)
        {
            var findings = new List<ImpactFinding>();

            foreach (var change in changes)
            {
                var tableLower  = change.Table.Trim().ToLowerInvariant();
                var columnLower = change.Column.Trim().ToLowerInvariant();

                switch (change.ChangeType)
                {
                    case SimulationChangeType.DropColumn:
                    case SimulationChangeType.RenameColumn:
                        AnalyseColumnChange(graph, change, tableLower, columnLower, findings);
                        break;

                    case SimulationChangeType.DropTable:
                    case SimulationChangeType.RenameTable:
                        AnalyseTableChange(graph, change, tableLower, findings);
                        break;

                    case SimulationChangeType.ChangeColumnType:
                        AnalyseColumnTypeChange(graph, change, tableLower, columnLower, findings);
                        break;
                }
            }

            var checklist = BuildChecklist(findings, changes);
            return new SimulationResult(changes, findings, checklist);
        }

        // ── Column-level analysis ─────────────────────────────────────────────

        private static void AnalyseColumnChange(
            LineageGraph graph, SchemaChange change,
            string tableLower, string columnLower,
            List<ImpactFinding> findings)
        {
            var verb = change.ChangeType == SimulationChangeType.DropColumn ? "dropped" : $"renamed → {change.NewName}";

            // 1. Column Mappings — source side
            foreach (var map in graph.ColumnMappings)
            {
                if (MatchesTable(map.SourceSchema, map.SourceTable, map.SourceComponentName, tableLower) &&
                    MatchesColumn(map.SourceColumnName, columnLower))
                {
                    var pkg = ResolvePackageName(graph, map.PackageId);
                    var task = ResolveTaskName(graph, map.TaskId);
                    findings.Add(new ImpactFinding
                    {
                        Severity     = ImpactSeverity.Critical,
                        Category     = "Column Mapping",
                        Location     = $"{pkg} / {task}",
                        AffectedObject = pkg,
                        Description  = $"Source column \"{FormatColumn(change.Table, change.Column)}\" is {verb}. " +
                                       $"Mapping → \"{FormatColumn(map.TargetTable, map.TargetColumnName)}\" will break."
                    });
                }
            }

            // 2. Column Mappings — target side
            foreach (var map in graph.ColumnMappings)
            {
                if (MatchesTable(map.TargetSchema, map.TargetTable, map.TargetComponentName, tableLower) &&
                    MatchesColumn(map.TargetColumnName, columnLower))
                {
                    var pkg  = ResolvePackageName(graph, map.PackageId);
                    var task = ResolveTaskName(graph, map.TaskId);
                    findings.Add(new ImpactFinding
                    {
                        Severity     = ImpactSeverity.Critical,
                        Category     = "Column Mapping",
                        Location     = $"{pkg} / {task}",
                        AffectedObject = pkg,
                        Description  = $"Target column \"{FormatColumn(change.Table, change.Column)}\" is {verb}. " +
                                       $"Mapping from \"{FormatColumn(map.SourceTable, map.SourceColumnName)}\" will break."
                    });
                }
            }

            // 3. Stored procedures referencing the column in SQL text
            foreach (var comp in graph.Components)
            {
                if (!string.IsNullOrEmpty(comp.SqlQueryOrTable) &&
                    comp.SqlQueryOrTable.Contains(change.Column, StringComparison.OrdinalIgnoreCase))
                {
                    var pkg  = ResolvePackageName(graph, comp.PackageId);
                    var task = ResolveTaskName(graph, comp.TaskId);
                    findings.Add(new ImpactFinding
                    {
                        Severity     = ImpactSeverity.Critical,
                        Category     = "Stored Procedure",
                        Location     = $"{pkg} / {task} / {comp.Name}",
                        AffectedObject = string.IsNullOrEmpty(comp.SqlQueryOrTable) ? pkg : comp.SqlQueryOrTable,
                        Description  = $"Component \"{comp.Name}\" in {task} has SQL referencing column \"{change.Column}\". " +
                                       $"Column will be {verb}."
                    });
                }
            }
        }

        // ── Table-level analysis ──────────────────────────────────────────────

        private static void AnalyseTableChange(
            LineageGraph graph, SchemaChange change,
            string tableLower,
            List<ImpactFinding> findings)
        {
            var verb = change.ChangeType == SimulationChangeType.DropTable ? "dropped" : $"renamed → {change.NewName}";

            // Source table references
            var sourceHits = graph.ColumnMappings
                .Where(m => MatchesTable(m.SourceSchema, m.SourceTable, m.SourceComponentName, tableLower))
                .GroupBy(m => (m.PackageId, m.TaskId));

            foreach (var grp in sourceHits)
            {
                var pkg  = ResolvePackageName(graph, grp.Key.PackageId);
                var task = ResolveTaskName(graph, grp.Key.TaskId);
                findings.Add(new ImpactFinding
                {
                    Severity     = ImpactSeverity.Critical,
                    Category     = "Column Mapping",
                    Location     = $"{pkg} / {task}",
                    AffectedObject = pkg,
                    Description  = $"Source table \"{change.Table}\" is {verb}. " +
                                   $"{grp.Count()} column mapping(s) in this task will break."
                });
            }

            // Target table references
            var targetHits = graph.ColumnMappings
                .Where(m => MatchesTable(m.TargetSchema, m.TargetTable, m.TargetComponentName, tableLower))
                .GroupBy(m => (m.PackageId, m.TaskId));

            foreach (var grp in targetHits)
            {
                var pkg  = ResolvePackageName(graph, grp.Key.PackageId);
                var task = ResolveTaskName(graph, grp.Key.TaskId);
                findings.Add(new ImpactFinding
                {
                    Severity     = ImpactSeverity.Critical,
                    Category     = "Column Mapping",
                    Location     = $"{pkg} / {task}",
                    AffectedObject = pkg,
                    Description  = $"Target table \"{change.Table}\" is {verb}. " +
                                   $"{grp.Count()} column mapping(s) in this task will break."
                });
            }

            // Component SQL references
            foreach (var comp in graph.Components)
            {
                // Match the simple table name or schema.table
                var tableSimple = change.Table.Split('.').Last();
                if (!string.IsNullOrEmpty(comp.SqlQueryOrTable) &&
                    (comp.SqlQueryOrTable.Contains(tableSimple, StringComparison.OrdinalIgnoreCase) ||
                     comp.SqlQueryOrTable.Contains(change.Table, StringComparison.OrdinalIgnoreCase)))
                {
                    var pkg  = ResolvePackageName(graph, comp.PackageId);
                    var task = ResolveTaskName(graph, comp.TaskId);
                    findings.Add(new ImpactFinding
                    {
                        Severity     = ImpactSeverity.Warning,
                        Category     = "Task",
                        Location     = $"{pkg} / {task} / {comp.Name}",
                        AffectedObject = pkg,
                        Description  = $"Component \"{comp.Name}\" SQL or table reference may reference \"{change.Table}\". Verify manually."
                    });
                }
            }
        }

        // ── Type change analysis ──────────────────────────────────────────────

        private static void AnalyseColumnTypeChange(
            LineageGraph graph, SchemaChange change,
            string tableLower, string columnLower,
            List<ImpactFinding> findings)
        {
            foreach (var map in graph.ColumnMappings)
            {
                bool isSource = MatchesTable(map.SourceSchema, map.SourceTable, map.SourceComponentName, tableLower)
                             && MatchesColumn(map.SourceColumnName, columnLower);
                bool isTarget = MatchesTable(map.TargetSchema, map.TargetTable, map.TargetComponentName, tableLower)
                             && MatchesColumn(map.TargetColumnName, columnLower);

                if (isSource || isTarget)
                {
                    var pkg  = ResolvePackageName(graph, map.PackageId);
                    var task = ResolveTaskName(graph, map.TaskId);
                    var side = isSource ? "Source" : "Target";
                    findings.Add(new ImpactFinding
                    {
                        Severity     = ImpactSeverity.Warning,
                        Category     = "Column Mapping",
                        Location     = $"{pkg} / {task}",
                        AffectedObject = pkg,
                        Description  = $"{side} column \"{FormatColumn(change.Table, change.Column)}\" type changes to \"{change.NewType}\". " +
                                       $"Verify implicit conversion in mapping does not cause data truncation or errors."
                    });
                }
            }
        }

        // ── Checklist builder ─────────────────────────────────────────────────

        private static List<ChecklistItem> BuildChecklist(
            List<ImpactFinding> findings, IReadOnlyList<SchemaChange> changes)
        {
            var items = new List<ChecklistItem>();

            foreach (var change in changes)
            {
                var tableSimple = change.Table.Split('.').Last();

                switch (change.ChangeType)
                {
                    case SimulationChangeType.RenameColumn:
                        items.Add(new ChecklistItem
                        {
                            Action = "Rename column in database schema",
                            Target = $"{change.Table}.{change.Column} → {change.NewName}",
                            Reason = "Source of truth — apply DDL change first",
                            IsRequired = true
                        });
                        break;

                    case SimulationChangeType.DropColumn:
                        items.Add(new ChecklistItem
                        {
                            Action = "Remove column from database schema",
                            Target = $"{change.Table}.{change.Column}",
                            Reason = "Apply DDL DROP COLUMN after all SSIS packages are updated",
                            IsRequired = true
                        });
                        break;

                    case SimulationChangeType.RenameTable:
                        items.Add(new ChecklistItem
                        {
                            Action = "Rename table in database schema",
                            Target = $"{change.Table} → {change.NewName}",
                            Reason = "Apply RENAME TABLE after all SSIS packages are updated",
                            IsRequired = true
                        });
                        break;

                    case SimulationChangeType.DropTable:
                        items.Add(new ChecklistItem
                        {
                            Action = "Drop table from database",
                            Target = change.Table,
                            Reason = "Apply DROP TABLE only after all dependent packages are removed or redirected",
                            IsRequired = true
                        });
                        break;

                    case SimulationChangeType.ChangeColumnType:
                        items.Add(new ChecklistItem
                        {
                            Action = "Alter column type in database schema",
                            Target = $"{change.Table}.{change.Column} → {change.NewType}",
                            Reason = "Verify data compatibility before altering",
                            IsRequired = true
                        });
                        break;
                }

                // Affected packages checklist
                var affectedPkgs = findings
                    .Where(f => f.Category == "Column Mapping" || f.Category == "Task")
                    .Select(f => f.AffectedObject)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(p => p.EndsWith(".dtsx", StringComparison.OrdinalIgnoreCase));

                foreach (var pkg in affectedPkgs)
                {
                    items.Add(new ChecklistItem
                    {
                        Action = "Update SSIS package column mapping",
                        Target = pkg,
                        Reason = $"Package references affected schema object",
                        IsRequired = true
                    });
                }

                // Stored procedure items
                var affectedProcs = findings
                    .Where(f => f.Category == "Stored Procedure")
                    .Select(f => f.AffectedObject)
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                foreach (var proc in affectedProcs)
                {
                    items.Add(new ChecklistItem
                    {
                        Action = "Review and update stored procedure",
                        Target = proc,
                        Reason = "Procedure SQL references the changed object",
                        IsRequired = true
                    });
                }

                if (findings.Any(f => f.Severity == ImpactSeverity.Warning))
                {
                    items.Add(new ChecklistItem
                    {
                        Action = "Re-run lineage scan and validate",
                        Target = "Full project scan",
                        Reason = "Confirm no residual implicit references after all changes are applied",
                        IsRequired = false
                    });
                }
            }

            // Deduplicate
            return items
                .GroupBy(i => $"{i.Action}|{i.Target}")
                .Select(g => g.First())
                .ToList();
        }

        // ── Markdown export ───────────────────────────────────────────────────

        /// <summary>Generate a migration checklist as a Markdown document.</summary>
        public static string GenerateMarkdown(SimulationResult result)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# What-If Simulation — Migration Checklist");
            sb.AppendLine();
            sb.AppendLine($"> Generated: {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine();

            sb.AppendLine("## Proposed Changes");
            sb.AppendLine();
            foreach (var c in result.Changes)
            {
                var desc = c.ChangeType switch
                {
                    SimulationChangeType.RenameColumn  => $"Rename **{c.Table}.{c.Column}** → `{c.NewName}`",
                    SimulationChangeType.DropColumn    => $"Drop **{c.Table}.{c.Column}**",
                    SimulationChangeType.RenameTable   => $"Rename table **{c.Table}** → `{c.NewName}`",
                    SimulationChangeType.DropTable     => $"Drop table **{c.Table}**",
                    SimulationChangeType.ChangeColumnType => $"Change **{c.Table}.{c.Column}** type → `{c.NewType}`",
                    _ => c.ChangeType.ToString()
                };
                sb.AppendLine($"- {desc}");
            }
            sb.AppendLine();

            sb.AppendLine("## Impact Summary");
            sb.AppendLine();
            sb.AppendLine($"| Metric | Count |");
            sb.AppendLine($"|--------|-------|");
            sb.AppendLine($"| Affected Packages | {result.AffectedPackages} |");
            sb.AppendLine($"| Broken Column Mappings | {result.AffectedMappings} |");
            sb.AppendLine($"| Stored Procedures to Review | {result.AffectedProcedures} |");
            sb.AppendLine($"| Tasks Affected | {result.AffectedTasks} |");
            sb.AppendLine();

            if (result.Findings.Any())
            {
                sb.AppendLine("## Findings");
                sb.AppendLine();
                sb.AppendLine("| Severity | Category | Location | Description |");
                sb.AppendLine("|----------|----------|----------|-------------|");
                foreach (var f in result.Findings.OrderBy(f => f.Severity))
                {
                    var sev = f.Severity switch
                    {
                        ImpactSeverity.Critical => "🔴 Critical",
                        ImpactSeverity.Warning  => "🟡 Warning",
                        _                       => "⚪ Info"
                    };
                    sb.AppendLine($"| {sev} | {f.Category} | {f.Location} | {f.Description} |");
                }
                sb.AppendLine();
            }

            sb.AppendLine("## Migration Checklist");
            sb.AppendLine();
            int i = 1;
            foreach (var item in result.Checklist)
            {
                var req = item.IsRequired ? "**[Required]**" : "*[Recommended]*";
                sb.AppendLine($"{i++}. {req} **{item.Action}** — `{item.Target}`");
                sb.AppendLine($"   > {item.Reason}");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static bool MatchesTable(string schema, string table, string componentName, string targetLower)
        {
            // Try schema.table combination
            if (!string.IsNullOrEmpty(table))
            {
                var qualified = string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";
                if (qualified.Equals(targetLower, StringComparison.OrdinalIgnoreCase)) return true;
                if (table.Equals(targetLower.Split('.').Last(), StringComparison.OrdinalIgnoreCase)) return true;
            }

            // Fall back to componentName (some components store schema.table here)
            if (!string.IsNullOrEmpty(componentName))
            {
                if (componentName.Equals(targetLower, StringComparison.OrdinalIgnoreCase)) return true;
                if (componentName.Split('.').Last().Equals(targetLower.Split('.').Last(), StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static bool MatchesColumn(string column, string targetLower) =>
            !string.IsNullOrEmpty(column) &&
            column.Equals(targetLower, StringComparison.OrdinalIgnoreCase);

        private static string ResolvePackageName(LineageGraph g, string packageId) =>
            g.Packages.Find(p => p.Id == packageId)?.Name ?? packageId;

        private static string ResolveTaskName(LineageGraph g, string taskId) =>
            g.Tasks.Find(t => t.Id == taskId)?.Name ?? taskId;

        private static string FormatColumn(string table, string column) =>
            string.IsNullOrEmpty(table) ? column : $"{table}.{column}";
    }
}
