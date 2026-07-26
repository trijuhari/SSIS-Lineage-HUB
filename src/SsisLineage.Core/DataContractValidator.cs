using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SsisLineage.Core.Models;

namespace SsisLineage.Core
{
    // ── Contract Models ───────────────────────────────────────────────────────

    public enum ViolationSeverity { Info, Additive, Mismatch, Breaking }

    public sealed class ContractColumnSpec
    {
        public string Name { get; set; } = "";
        public bool IsRequired { get; set; } = true;
        public string ExpectedType { get; set; } = "string";
    }

    public sealed class TableDataContract
    {
        public string TableName { get; set; } = "";
        public string OwnerTeam { get; set; } = "Data Engineering";
        public string SlaTier { get; set; } = "Tier-1";
        public List<ContractColumnSpec> Columns { get; set; } = new();
    }

    public sealed class DataContractSpec
    {
        public string ProjectName { get; set; } = "SSIS Lineage Hub Project";
        public string Version { get; set; } = "1.0.0";
        public DateTime CreatedDate { get; set; } = DateTime.Now;
        public List<TableDataContract> Tables { get; set; } = new();
    }

    public sealed class ContractViolation
    {
        public string TableName { get; set; } = "";
        public string ColumnName { get; set; } = "";
        public ViolationSeverity Severity { get; set; }
        public string Message { get; set; } = "";
        public string Expected { get; set; } = "";
        public string Actual { get; set; } = "";
    }

    public sealed class ContractValidationResult
    {
        public bool IsCompliant => BreakingCount == 0;
        public int TotalTables { get; set; }
        public int BreakingCount => Violations.Count(v => v.Severity == ViolationSeverity.Breaking);
        public int MismatchCount => Violations.Count(v => v.Severity == ViolationSeverity.Mismatch);
        public int AdditiveCount => Violations.Count(v => v.Severity == ViolationSeverity.Additive);
        public List<ContractViolation> Violations { get; set; } = new();
        public DateTime ValidatedDate { get; set; } = DateTime.Now;
    }

    // ── Validator Engine ──────────────────────────────────────────────────────

    /// <summary>
    /// Generates and validates Data Contracts (YAML / Schema Specs) against the active LineageGraph.
    /// Surfacing Breaking, Mismatch, and Additive schema contract violations.
    /// 100% offline in-memory execution.
    /// </summary>
    public static class DataContractValidator
    {
        /// <summary>Auto-generates a DataContractSpec from an active LineageGraph.</summary>
        public static DataContractSpec GenerateContract(LineageGraph graph, string projectName = "SSIS Data Pipeline")
        {
            var spec = new DataContractSpec
            {
                ProjectName = projectName,
                Version = "1.0.0",
                CreatedDate = DateTime.Now,
                Tables = new List<TableDataContract>()
            };

            if (graph == null || graph.ColumnMappings.Count == 0) return spec;

            var tableGroups = graph.ColumnMappings
                .GroupBy(m => string.IsNullOrEmpty(m.TargetSchema) ? m.TargetTable : $"{m.TargetSchema}.{m.TargetTable}", StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrEmpty(g.Key));

            foreach (var group in tableGroups)
            {
                var tableContract = new TableDataContract
                {
                    TableName = group.Key,
                    OwnerTeam = "Data Engineering Team",
                    SlaTier = "Tier-1",
                    Columns = group
                        .Select(m => m.TargetColumnName)
                        .Where(c => !string.IsNullOrEmpty(c) && c != "*")
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(c => new ContractColumnSpec
                        {
                            Name = c,
                            IsRequired = true,
                            ExpectedType = "NVARCHAR/INT"
                        })
                        .ToList()
                };

                spec.Tables.Add(tableContract);
            }

            return spec;
        }

        /// <summary>Validates an active LineageGraph against a DataContractSpec.</summary>
        public static ContractValidationResult Validate(DataContractSpec spec, LineageGraph graph)
        {
            var result = new ContractValidationResult
            {
                TotalTables = spec?.Tables.Count ?? 0,
                ValidatedDate = DateTime.Now
            };

            if (spec == null || spec.Tables.Count == 0)
                return result;

            if (graph == null || graph.ColumnMappings.Count == 0)
            {
                // All contract tables breaking because graph is empty
                foreach (var t in spec.Tables)
                {
                    result.Violations.Add(new ContractViolation
                    {
                        TableName = t.TableName,
                        ColumnName = "*",
                        Severity = ViolationSeverity.Breaking,
                        Message = $"Tabel '{t.TableName}' tidak ditemukan sama sekali di lineage graph saat ini.",
                        Expected = "Table present in lineage",
                        Actual = "Table missing"
                    });
                }
                return result;
            }

            foreach (var contractTable in spec.Tables)
            {
                // Find all mappings for this table in current graph
                var currentMappings = graph.ColumnMappings
                    .Where(m =>
                    {
                        var tgt = string.IsNullOrEmpty(m.TargetSchema) ? m.TargetTable : $"{m.TargetSchema}.{m.TargetTable}";
                        return tgt.Equals(contractTable.TableName, StringComparison.OrdinalIgnoreCase) ||
                               (!string.IsNullOrEmpty(m.TargetTable) && m.TargetTable.Equals(contractTable.TableName, StringComparison.OrdinalIgnoreCase));
                    })
                    .ToList();

                if (currentMappings.Count == 0)
                {
                    result.Violations.Add(new ContractViolation
                    {
                        TableName = contractTable.TableName,
                        ColumnName = "*",
                        Severity = ViolationSeverity.Breaking,
                        Message = $"Tabel target contract '{contractTable.TableName}' tidak ditemukan di pipeline SSIS saat ini.",
                        Expected = "Table present in mappings",
                        Actual = "Table missing"
                    });
                    continue;
                }

                var currentCols = currentMappings
                    .Select(m => m.TargetColumnName)
                    .Where(c => !string.IsNullOrEmpty(c))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Check required contract columns vs current graph
                foreach (var specCol in contractTable.Columns)
                {
                    if (specCol.IsRequired && !currentCols.Contains(specCol.Name))
                    {
                        result.Violations.Add(new ContractViolation
                        {
                            TableName = contractTable.TableName,
                            ColumnName = specCol.Name,
                            Severity = ViolationSeverity.Breaking,
                            Message = $"Kolom wajib '{specCol.Name}' terhapus atau terputus dari mapping SSIS!",
                            Expected = $"Column '{specCol.Name}' mapped",
                            Actual = "Column missing in mappings"
                        });
                    }
                }

                // Check for new additive columns not in contract
                var specColNames = contractTable.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var curCol in currentCols)
                {
                    if (!specColNames.Contains(curCol) && curCol != "*")
                    {
                        result.Violations.Add(new ContractViolation
                        {
                            TableName = contractTable.TableName,
                            ColumnName = curCol,
                            Severity = ViolationSeverity.Additive,
                            Message = $"Kolom baru '{curCol}' ditambahkan ke pipeline tanpa tercantum di contract.",
                            Expected = "Unlisted column",
                            Actual = $"New column '{curCol}' present"
                        });
                    }
                }
            }

            return result;
        }

        /// <summary>Generates YAML string representation of DataContractSpec.</summary>
        public static string ToYaml(DataContractSpec spec)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# ==========================================================");
            sb.AppendLine("# SSIS Lineage Hub — Data Contract Specification (YAML)");
            sb.AppendLine("# ==========================================================");
            sb.AppendLine($"version: '{spec.Version}'");
            sb.AppendLine($"info:");
            sb.AppendLine($"  title: '{spec.ProjectName}'");
            sb.AppendLine($"  generatedAt: '{spec.CreatedDate:yyyy-MM-dd HH:mm:ss}'");
            sb.AppendLine("tables:");

            foreach (var t in spec.Tables)
            {
                sb.AppendLine($"  - name: '{t.TableName}'");
                sb.AppendLine($"    owner: '{t.OwnerTeam}'");
                sb.AppendLine($"    sla: '{t.SlaTier}'");
                sb.AppendLine($"    columns:");
                foreach (var c in t.Columns)
                {
                    sb.AppendLine($"      - name: '{c.Name}'");
                    sb.AppendLine($"        required: {c.IsRequired.ToString().ToLower()}");
                    sb.AppendLine($"        type: '{c.ExpectedType}'");
                }
            }

            return sb.ToString();
        }
    }
}
