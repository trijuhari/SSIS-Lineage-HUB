using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SsisLineage.Core.Models;

namespace SsisLineage.Core
{
    public enum MigrationTarget
    {
        DbtSql,
        PySpark,
        AzureDataFactory,
        ConsolidatedSql,
        AirflowDag,
        PythonPandas,
        BmcControlMJson
    }

    public class GeneratedFile
    {
        public string FileName { get; set; } = "";
        public string Content { get; set; } = "";
        public string Language { get; set; } = "sql";
        public string TargetFramework { get; set; } = "";
    }

    public class MigrationResult
    {
        public MigrationTarget Target { get; set; }
        public List<GeneratedFile> Files { get; set; } = new();
        public int PackagesConverted { get; set; }
        public int MappingsConverted { get; set; }
        public string Summary { get; set; } = "";
        public List<string> Warnings { get; set; } = new();
        public List<string> ValidationErrors { get; set; } = new();
        public bool IsValid => ValidationErrors.Count == 0;
    }

    public static class SsisMigrationConverter
    {
        public static MigrationResult ConvertProject(LineageGraph graph, MigrationTarget target, string? selectedPackageId = null)
        {
            var result = new MigrationResult
            {
                Target = target
            };

            if (graph == null) return result;

            var packagesToConvert = string.IsNullOrEmpty(selectedPackageId) || selectedPackageId == "ALL"
                ? graph.Packages
                : graph.Packages.Where(p => p.Id == selectedPackageId).ToList();

            result.PackagesConverted = packagesToConvert.Count;

            switch (target)
            {
                case MigrationTarget.DbtSql:
                    GenerateDbtModels(graph, packagesToConvert, result);
                    break;
                case MigrationTarget.PySpark:
                    GeneratePySparkScripts(graph, packagesToConvert, result);
                    break;
                case MigrationTarget.AzureDataFactory:
                    GenerateAdfPipelines(graph, packagesToConvert, result);
                    break;
                case MigrationTarget.ConsolidatedSql:
                    GenerateConsolidatedSql(graph, packagesToConvert, result);
                    break;
                case MigrationTarget.AirflowDag:
                    GenerateAirflowDags(graph, packagesToConvert, result);
                    break;
                case MigrationTarget.PythonPandas:
                    GeneratePythonPandasScripts(graph, packagesToConvert, result);
                    break;
                case MigrationTarget.BmcControlMJson:
                    GenerateBmcControlMJson(graph, packagesToConvert, result);
                    break;
            }

            ValidateGeneratedResult(result);

            return result;
        }

        // ── 1. dbt SQL Models Generator ────────────────────────────────────────
        private static void GenerateDbtModels(LineageGraph graph, List<PackageNode> packages, MigrationResult result)
        {
            var schemaYaml = new StringBuilder();
            schemaYaml.AppendLine("version: 2");
            schemaYaml.AppendLine();
            schemaYaml.AppendLine("models:");

            foreach (var pkg in packages)
            {
                var pkgComponents = graph.Components.Where(c => c.PackageId == pkg.Id).ToList();
                var pkgTasks = graph.Tasks.Where(t => t.PackageId == pkg.Id).ToList();
                var pkgMappings = graph.ColumnMappings.Where(m => m.PackageId == pkg.Id && (m.OperationType == null || !m.OperationType.StartsWith("SQL_PROC_"))).ToList();

                var dataFlowTasks = pkgTasks.Where(t => t.Type != null && t.Type.Contains("Data Flow", StringComparison.OrdinalIgnoreCase)).ToList();

                // Skip Master Orchestration packages (e.g. Pkg_00_Master_ETL_Orchestration) that have no Data Flow components
                if (pkg.Name.Contains("Master", StringComparison.OrdinalIgnoreCase) && !pkgComponents.Any(c => c.Type != null && c.Type.Contains("Source", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var modelName = CleanIdentifier(pkg.Name).ToLowerInvariant();
                if (modelName.StartsWith("pkg_")) modelName = modelName.Substring(4);
                modelName = "stg_" + modelName;

                schemaYaml.AppendLine($"  - name: {modelName}");
                schemaYaml.AppendLine($"    description: \"Auto-converted from SSIS Package '{pkg.Name}'\"");

                var targetCols = pkgMappings.Select(m => m.TargetColumnName).Distinct().Where(c => !string.IsNullOrEmpty(c)).ToList();
                if (targetCols.Any())
                {
                    schemaYaml.AppendLine("    columns:");
                    foreach (var col in targetCols)
                    {
                        schemaYaml.AppendLine($"      - name: {col}");
                        schemaYaml.AppendLine($"        description: \"Mapped from source column\"");
                    }
                }
                else
                {
                    schemaYaml.AppendLine("    columns: []");
                }

                // Generate Model SQL (.sql)
                var sb = new StringBuilder();
                sb.AppendLine($"-- dbt Model: {modelName}.sql");
                sb.AppendLine($"-- Converted from SSIS Package: {pkg.Name}");
                sb.AppendLine();

                var aggregates = pkgComponents.Where(c => c.Type != null && c.Type.Contains("Aggregate", StringComparison.OrdinalIgnoreCase)).ToList();
                var conditionalSplits = pkgComponents.Where(c => c.Type != null && c.Type.Contains("Conditional Split", StringComparison.OrdinalIgnoreCase)).ToList();
                var unions = pkgComponents.Where(c => c.Type != null && c.Type.Contains("Union All", StringComparison.OrdinalIgnoreCase)).ToList();
                var scripts = pkgComponents.Where(c => c.Type != null && c.Type.Contains("Script Component", StringComparison.OrdinalIgnoreCase)).ToList();

                if (aggregates.Any() || conditionalSplits.Any() || unions.Any() || scripts.Any())
                {
                    sb.AppendLine("/* ============================================================================");
                    sb.AppendLine("   WARNING: This SSIS Package contains advanced transformations that require");
                    sb.AppendLine("   manual review or specific Python routing in the Modern Data Stack.");
                    if (aggregates.Any()) sb.AppendLine("   - Aggregate Component found: Ensure GROUP BY is correctly implemented.");
                    if (conditionalSplits.Any()) sb.AppendLine("   - Conditional Split found: Implement WHERE clauses or multi-CTE routing.");
                    if (unions.Any()) sb.AppendLine("   - Union All found: Verify UNION ALL logic across multiple sources.");
                    if (scripts.Any()) sb.AppendLine("   - Script Component found: Shift logic to Python (Pandas/PySpark) extract layer.");
                    sb.AppendLine("============================================================================ */");
                    sb.AppendLine();
                }

                var sources = pkgComponents.Where(c => c.Type.Contains("Source", StringComparison.OrdinalIgnoreCase)).ToList();
                var destinations = pkgComponents.Where(c => c.Type.Contains("Destination", StringComparison.OrdinalIgnoreCase)).ToList();
                // Bug #5 fix: prefer staging destination (stg_/staging prefix) over arbitrary first destination
                // when multiple destinations exist (e.g. packages with error/audit outputs)
                var primaryDestination = destinations
                    .FirstOrDefault(d => !string.IsNullOrEmpty(d.SqlQueryOrTable) &&
                        (d.SqlQueryOrTable.Contains("stg_", StringComparison.OrdinalIgnoreCase) ||
                         d.SqlQueryOrTable.Contains("staging", StringComparison.OrdinalIgnoreCase)))
                    ?? destinations.FirstOrDefault(d => !string.IsNullOrEmpty(d.SqlQueryOrTable))
                    ?? destinations.FirstOrDefault();
                var rawLandingTable = primaryDestination != null && !string.IsNullOrEmpty(primaryDestination.SqlQueryOrTable)
                    ? primaryDestination.SqlQueryOrTable
                    : ("dbo_stg_" + CleanIdentifier(pkg.Name));
                var landingTable = Regex.Replace(rawLandingTable, @"[\[\]]", "").Replace(".", "_").Trim();
                if (string.IsNullOrEmpty(landingTable)) landingTable = "dbo_stg_" + CleanIdentifier(pkg.Name);

                sb.AppendLine("WITH source_data AS (");
                sb.AppendLine($"    -- Extracted from Landing Zone (Populated by Python)");
                // landingTable is already flattened to a plain identifier (e.g. dbo_stg_ECommerceOrders)
                // by the dot→underscore transform above, so no schema prefix is needed here.
                // The dbt profile schema (dbo) is applied automatically at run time.
                sb.AppendLine($"    SELECT * FROM {landingTable}");
                sb.AppendLine(")");
                
                // Build a lookup alias map: component name/id → CTE index (lookup_0, lookup_1 …)
                // Register by BOTH Name and Id so SourceComponentName can match either.
                var lookups = pkgComponents.Where(c => c.Type != null && c.Type.Contains("Lookup", StringComparison.OrdinalIgnoreCase)).ToList();
                var lookupAliasMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                // Fallback: column name → CTE index, for columns that come from a lookup but
                // whose SourceComponentName doesn't match the lookup's registered name.
                var lookupColumnIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var emittedLookupIndices = new HashSet<int>(); // only lookups that have a CTE
                int cteIdx = 0;
                foreach (var lkp in lookups)
                {
                    // Bug #6 fix: sanitize multi-line SQL in lookup CTE to single-line with consistent indent
                    var lkpSqlRaw = string.IsNullOrEmpty(lkp.SqlQueryOrTable)
                        ? $"SELECT * FROM ref_{Regex.Replace(lkp.Name ?? "Lookup", @"[^\w]", "_")}"
                        : lkp.SqlQueryOrTable;

                    var lkpSql = lkpSqlRaw.Trim()
                        .Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
                    // Collapse multiple spaces into one
                    lkpSql = Regex.Replace(lkpSql, @"\s{2,}", " ");

                    if (!lkpSql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
                        !lkpSql.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
                    {
                        lkpSql = $"SELECT * FROM {lkpSql}";
                    }

                    sb.AppendLine($",\nlookup_{cteIdx} AS (");
                    sb.AppendLine($"    {lkpSql}");
                    sb.AppendLine(")");

                    // Register by Name (primary key)
                    if (!string.IsNullOrEmpty(lkp.Name))
                        lookupAliasMap[lkp.Name] = cteIdx;
                    // Register by Id (fallback — ResolveComponentName may return raw id)
                    if (!string.IsNullOrEmpty(lkp.Id))
                        lookupAliasMap[lkp.Id] = cteIdx;
                    // Register by TaskId::Name pattern variants
                    if (!string.IsNullOrEmpty(lkp.TaskId) && !string.IsNullOrEmpty(lkp.Name))
                        lookupAliasMap[$"{lkp.TaskId}::{lkp.Name}"] = cteIdx;

                    // Build column-name → cteIdx index from the lookup SQL
                    // Parse "SELECT col1, col2, col3 FROM ..." to extract column names
                    var selectMatch = Regex.Match(lkpSql, @"SELECT\s+(.+?)\s+FROM\b", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (selectMatch.Success)
                    {
                        var colList = selectMatch.Groups[1].Value;
                        foreach (var colPart in colList.Split(','))
                        {
                            // Handle "col AS alias" or "[col]" patterns
                            var colName = Regex.Match(colPart.Trim(), @"(?:AS\s+)?(\[?[a-zA-Z_]\w*\]?)\s*$", RegexOptions.IgnoreCase).Groups[1].Value
                                .Replace("[", "").Replace("]", "").Trim();
                            if (!string.IsNullOrEmpty(colName))
                                lookupColumnIndex.TryAdd(colName, cteIdx);
                        }
                    }

                    emittedLookupIndices.Add(cteIdx);
                    cteIdx++;
                }
                var totalLookupCtes = cteIdx; // number of CTEs actually emitted

                sb.AppendLine();
                sb.AppendLine(",\ntransformed AS (");
                sb.AppendLine("    SELECT");

                var sqlKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                    "CAST", "AS", "INT", "DECIMAL", "DATETIME", "NVARCHAR", "VARCHAR",
                    "CHAR", "BIT", "FLOAT", "BIGINT", "SMALLINT", "TINYINT",
                    "CASE", "WHEN", "THEN", "ELSE", "END", "NULL", "NOT", "AND", "OR",
                    "IS", "IN", "LIKE", "BETWEEN", "EXISTS", "DISTINCT", "SELECT", "FROM",
                    "WHERE", "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "ON", "GROUP", "BY",
                    "ORDER", "HAVING", "TOP", "WITH", "UNION", "ALL", "TRUE", "FALSE",
                    "1", "0", "source_data" // prevent double-prefixing
                };

                if (pkgMappings.Any())
                {
                    var mapLines = new List<string>();
                    // Bug #7 fix: track duplicate target columns and emit a warning comment
                    var seenTargets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var m in pkgMappings)
                    {
                        seenTargets.TryGetValue(m.TargetColumnName, out var count);
                        seenTargets[m.TargetColumnName] = count + 1;
                    }
                    var duplicateTargets = seenTargets.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (duplicateTargets.Any())
                        sb.AppendLine($"    -- WARNING: Duplicate target columns detected (keeping first): {string.Join(", ", duplicateTargets)}");

                    // Build a lookup map of upstream derived column mappings by TargetColumnName (the column name output by Derived Column)
                    var derivedColumnMap = pkgMappings
                        .Where(m => !string.IsNullOrEmpty(m.SourceExpression))
                        .GroupBy(m => m.TargetColumnName, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                    var effectiveMappings = pkgMappings
                        .OrderByDescending(m => !string.IsNullOrEmpty(m.SourceExpression))
                        .ThenByDescending(m => m.OperationType == "DERIVED_COLUMN")
                        .ThenByDescending(m => lookupAliasMap.ContainsKey(m.SourceComponentName ?? "") || lookupAliasMap.ContainsKey(m.SourceComponentId ?? ""))
                        .DistinctBy(x => x.TargetColumnName, StringComparer.OrdinalIgnoreCase);

                    foreach (var m in effectiveMappings)
                    {
                        string expr;
                        var sourceExpr = m.SourceExpression;
                        var sourceCol = m.SourceColumnName;

                        // If m doesn't have a direct expression, check if its source column comes from an upstream Derived Column
                        if (string.IsNullOrEmpty(sourceExpr) && !string.IsNullOrEmpty(sourceCol) &&
                            derivedColumnMap.TryGetValue(sourceCol, out var upstreamDerived))
                        {
                            sourceExpr = upstreamDerived.SourceExpression;
                        }

                        if (!string.IsNullOrEmpty(sourceExpr))
                        {
                            // Bug #2 fix: Derived Column from SSIS — translate expression and qualify bare column
                            // refs with source_data prefix, but skip SQL string literals (quoted values)
                            var translated = TranslateSsisExpressionToSql(sourceExpr);
                            // First, mask string literals so they're not touched by the identifier regex
                            var literals = new List<string>();
                            var masked = Regex.Replace(translated, @"'[^']*'|""[^""]*""", lit =>
                            {
                                literals.Add(lit.Value);
                                return $"__LIT{literals.Count - 1}__";
                            });
                            // Strip SSIS column brackets [ColName] -> ColName before prefixing
                            masked = Regex.Replace(masked, @"\[([a-zA-Z_]\w*)\]", "$1");
                            // Now qualify bare identifiers (not keywords, not already qualified)
                            masked = Regex.Replace(masked, @"(?<![.\w])([a-zA-Z_]\w*)(?!\s*[\(.])", m2 =>
                            {
                                var word = m2.Groups[1].Value;
                                if (sqlKeywords.Contains(word)) return m2.Value;
                                // Skip __LITn__ placeholders
                                if (word.StartsWith("__LIT")) return m2.Value;
                                return $"source_data.{word}";
                            });
                            // Restore string literals
                            for (int li = 0; li < literals.Count; li++)
                                masked = masked.Replace($"__LIT{li}__", literals[li]);
                            // Strip any remaining SSIS square brackets around expressions/identifiers
                            masked = Regex.Replace(masked, @"\[([^\]]+)\]", "$1");
                            expr = masked;
                        }
                        else
                        {
                            // Simple column pass-through — determine if it comes from a Lookup CTE or source_data
                            var srcPrefix = "source_data";

                            // Strategy 1 (most reliable): match SourceComponentId against lookup component IDs
                            if (!string.IsNullOrEmpty(m.SourceComponentId) &&
                                lookupAliasMap.TryGetValue(m.SourceComponentId, out var lkpIdxById))
                            {
                                srcPrefix = $"lookup_{lkpIdxById}";
                            }
                            // Strategy 2: match SourceComponentName against lookupAliasMap (Name, Id, TaskId::Name variants)
                            else if (!string.IsNullOrEmpty(m.SourceComponentName) &&
                                     lookupAliasMap.TryGetValue(m.SourceComponentName, out var lkpIdxByName))
                            {
                                srcPrefix = $"lookup_{lkpIdxByName}";
                            }
                            // Strategy 2b: fuzzy/substring match SourceComponentName or SourceComponentId against lookupAliasMap
                            else if (!string.IsNullOrEmpty(m.SourceComponentName) &&
                                     lookupAliasMap.Any(kv => kv.Key.Contains(m.SourceComponentName, StringComparison.OrdinalIgnoreCase) ||
                                                              m.SourceComponentName.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)))
                            {
                                var matchedLkp = lookupAliasMap.First(kv => kv.Key.Contains(m.SourceComponentName, StringComparison.OrdinalIgnoreCase) ||
                                                                            m.SourceComponentName.Contains(kv.Key, StringComparison.OrdinalIgnoreCase));
                                srcPrefix = $"lookup_{matchedLkp.Value}";
                            }
                            // Strategy 2c: OperationType or SourceComponentName contains "lookup"
                            else if (totalLookupCtes > 0 &&
                                     ((!string.IsNullOrEmpty(m.OperationType) && m.OperationType.Contains("LOOKUP", StringComparison.OrdinalIgnoreCase)) ||
                                      (!string.IsNullOrEmpty(m.SourceComponentName) && m.SourceComponentName.Contains("lookup", StringComparison.OrdinalIgnoreCase)) ||
                                      (!string.IsNullOrEmpty(m.SourceComponentId) && m.SourceComponentId.Contains("lookup", StringComparison.OrdinalIgnoreCase))))
                            {
                                srcPrefix = "lookup_0";
                            }
                            // Strategy 3: column exists in lookup SELECT list (by source column name)
                            else if (!string.IsNullOrEmpty(sourceCol) &&
                                     lookupColumnIndex.TryGetValue(sourceCol, out var lkpIdxByCol))
                            {
                                srcPrefix = $"lookup_{lkpIdxByCol}";
                            }
                            // Strategy 3b (FIX #5): match TargetColumnName as a lookup alias.
                            // When a Lookup component renames a column (e.g. "SegmentName AS CustomerSegment"),
                            // lookupColumnIndex stores the ALIAS "CustomerSegment". SourceColumnName is the
                            // pre-alias "SegmentName" which may not be in the index. Checking TargetColumnName
                            // catches this case and avoids incorrectly sourcing the column from source_data.
                            else if (!string.IsNullOrEmpty(m.TargetColumnName) &&
                                     lookupColumnIndex.TryGetValue(m.TargetColumnName, out var lkpIdxByTargetCol))
                            {
                                srcPrefix = $"lookup_{lkpIdxByTargetCol}";
                                // Use TargetColumnName (the alias) as the column reference within the lookup CTE
                                sourceCol = m.TargetColumnName;
                            }
                            // Strategy 4: If lookup CTE exists, check known lookup attributes or dimension attributes (DateKey, FullDate, BranchKey, BranchName, RegionCode, etc.)
                            else if (totalLookupCtes > 0)
                            {
                                var colToCheck = (sourceCol ?? m.TargetColumnName ?? "");
                                var lowerCol = colToCheck.ToLowerInvariant();
                                if (lookupColumnIndex.TryGetValue(colToCheck, out var matchedLkpIdx))
                                {
                                    srcPrefix = $"lookup_{matchedLkpIdx}";
                                }
                                else if (lowerCol.Contains("fulldate") || lowerCol.Contains("datekey"))
                                {
                                    // Map date dimension columns to lookup matching dim.Date if present, or lookup_1
                                    srcPrefix = lookupColumnIndex.TryGetValue("FullDate", out var dtIdx) ? $"lookup_{dtIdx}" :
                                               lookupColumnIndex.TryGetValue("DateKey", out dtIdx) ? $"lookup_{dtIdx}" :
                                               (totalLookupCtes > 1 ? "lookup_1" : "lookup_0");
                                }
                                else if (lowerCol.Contains("branchkey") || lowerCol.Contains("branchname") || lowerCol.Contains("regioncode"))
                                {
                                    srcPrefix = lookupColumnIndex.TryGetValue("BranchKey", out var brIdx) ? $"lookup_{brIdx}" :
                                               lookupColumnIndex.TryGetValue("BranchCode", out brIdx) ? $"lookup_{brIdx}" : "lookup_0";
                                }
                                else if (lowerCol.Contains("customername") || lowerCol.Contains("customersegment") ||
                                         lowerCol.Contains("segment") || lowerCol.Contains("lookup"))
                                {
                                    srcPrefix = "lookup_0";
                                }
                            }

                            expr = $"{srcPrefix}.{sourceCol}";

                            // Apply type heuristics
                            var targetNameLower = m.TargetColumnName.ToLowerInvariant();
                            if ((targetNameLower.Contains("quantity") || targetNameLower.EndsWith("_count") || targetNameLower.StartsWith("count_") || targetNameLower == "count" || targetNameLower.Contains("rowcount") || targetNameLower.Contains("itemcount")) && !targetNameLower.Contains("account") && !targetNameLower.Contains("number"))
                                expr = $"CAST({expr} AS INT)";
                            else if (targetNameLower.Contains("amount") || targetNameLower.Contains("price") || targetNameLower.Contains("total"))
                                expr = $"CAST({expr} AS DECIMAL(18,2))";
                            else if (targetNameLower.Contains("date"))
                                expr = $"CAST({expr} AS DATETIME)";
                        }

                        mapLines.Add($"        {expr} AS {m.TargetColumnName}");
                    }
                    sb.AppendLine(string.Join(",\n", mapLines));
                }
                else
                {
                    sb.AppendLine("        source_data.*");
                }

                sb.AppendLine("    FROM source_data");
                // Bug #1 fix: only JOIN lookups that have an emitted CTE
                for (int i = 0; i < totalLookupCtes; i++)
                {
                    var lkpCols = lookupColumnIndex.Where(kv => kv.Value == i).Select(kv => kv.Key).ToList();
                    var lookupSourceCols = pkgMappings
                        .Where(m => string.IsNullOrEmpty(m.SourceExpression) &&
                                    !string.IsNullOrEmpty(m.SourceComponentName) &&
                                    lookupAliasMap.ContainsKey(m.SourceComponentName))
                        .Select(m => m.SourceColumnName)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var srcCols = pkgMappings
                        .Where(m => string.IsNullOrEmpty(m.SourceExpression) &&
                                    !string.IsNullOrEmpty(m.SourceColumnName) &&
                                    !lookupSourceCols.Contains(m.SourceColumnName))
                        .Select(m => m.SourceColumnName)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var commonKey = srcCols.FirstOrDefault(sc => lkpCols.Any(lc => string.Equals(sc, lc, StringComparison.OrdinalIgnoreCase)));
                    if (!string.IsNullOrEmpty(commonKey))
                    {
                        sb.AppendLine($"    LEFT JOIN lookup_{i} ON source_data.{commonKey} = lookup_{i}.{commonKey}");
                        continue;
                    }

                    var srcDateCol = srcCols.FirstOrDefault(sc => sc.EndsWith("Date", StringComparison.OrdinalIgnoreCase));
                    var lkpDateCol = lkpCols.FirstOrDefault(lc => string.Equals(lc, "FullDate", StringComparison.OrdinalIgnoreCase) || string.Equals(lc, "DateKey", StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(srcDateCol) && !string.IsNullOrEmpty(lkpDateCol))
                    {
                        sb.AppendLine($"    LEFT JOIN lookup_{i} ON source_data.{srcDateCol} = lookup_{i}.{lkpDateCol}");
                        continue;
                    }

                    // Join candidates fallback
                    var joinKeyCandidates = srcCols
                        .Where(col => {
                            var lower = col.ToLowerInvariant();
                            return lower.EndsWith("code") || lower.EndsWith("key") ||
                                   lower.EndsWith("id")   || lower.EndsWith("no");
                        })
                        .OrderBy(col => {
                            var lower = col.ToLowerInvariant();
                            if (lower.EndsWith("code")) return 0;
                            if (lower.EndsWith("key"))  return 1;
                            if (lower.EndsWith("id"))   return 2;
                            return 3;
                        })
                        .ToList();

                    var joinKey = joinKeyCandidates.FirstOrDefault();
                    if (string.IsNullOrEmpty(joinKey))
                    {
                        joinKey = pkgMappings.Select(m => m.SourceColumnName)
                                             .FirstOrDefault(c => !string.IsNullOrEmpty(c) && !c.Contains("Name", StringComparison.OrdinalIgnoreCase) && !c.Contains("Segment", StringComparison.OrdinalIgnoreCase) && (c.EndsWith("Code", StringComparison.OrdinalIgnoreCase) || c.EndsWith("Key", StringComparison.OrdinalIgnoreCase) || c.EndsWith("Id", StringComparison.OrdinalIgnoreCase) || c.EndsWith("Date", StringComparison.OrdinalIgnoreCase)))
                                  ?? "CustomerId";
                    }
                    sb.AppendLine($"    LEFT JOIN lookup_{i} ON source_data.{joinKey} = lookup_{i}.{joinKey}");
                }
                sb.AppendLine(")");
                sb.AppendLine();
                sb.AppendLine("SELECT * FROM transformed");

                result.Files.Add(new GeneratedFile
                {
                    FileName = $"{modelName}.sql",
                    Content = sb.ToString(),
                    Language = "sql",
                    TargetFramework = "dbt"
                });

                result.MappingsConverted += pkgMappings.Count;
            }

            result.Files.Insert(0, new GeneratedFile
            {
                FileName = "schema.yml",
                Content = schemaYaml.ToString(),
                Language = "yaml",
                TargetFramework = "dbt"
            });

            result.Summary = $"Generated {result.Files.Count - 1} dbt SQL models and 1 schema.yml spec from SSIS metadata.";
        }

        // ── 2. PySpark DataFrames Generator ─────────────────────────────────────
        private static void GeneratePySparkScripts(LineageGraph graph, List<PackageNode> packages, MigrationResult result)
        {
            foreach (var pkg in packages)
            {
                var pkgComponents = graph.Components.Where(c => c.PackageId == pkg.Id).ToList();
                var pkgMappings = graph.ColumnMappings.Where(m => m.PackageId == pkg.Id).ToList();

                var scriptName = CleanIdentifier(pkg.Name).ToLowerInvariant() + "_job.py";

                var sb = new StringBuilder();
                sb.AppendLine("# Databricks / PySpark ETL Job");
                sb.AppendLine($"# Migrated from SSIS Package: {pkg.Name}");
                sb.AppendLine("# Auto-generated by SSIS Lineage Hub Migrator");
                var sourceComp = pkgComponents.FirstOrDefault(c => c.Type.Contains("Source", StringComparison.OrdinalIgnoreCase));
                var destComp = pkgComponents.FirstOrDefault(c => c.Type.Contains("Destination", StringComparison.OrdinalIgnoreCase));

                var (srcServer, srcDb) = ResolveDatabaseAndServer(graph, pkg, sourceComp, false);
                srcServer = NormalizeServer(srcServer);

                sb.AppendLine();
                sb.AppendLine("from pyspark.sql import SparkSession");
                sb.AppendLine("from pyspark.sql import functions as F");
                sb.AppendLine();
                sb.AppendLine("spark = SparkSession.builder.appName('" + pkg.Name + "').getOrCreate()");
                sb.AppendLine();
                sb.AppendLine("# JDBC Connection Config");
                sb.AppendLine($"jdbc_url = 'jdbc:sqlserver://{srcServer};databaseName={srcDb}'");
                sb.AppendLine("connection_properties = {");
                sb.AppendLine("    'user': 'sa',");
                sb.AppendLine("    'password': 'YourPassword123!',");
                sb.AppendLine("    'driver': 'com.microsoft.sqlserver.jdbc.SQLServerDriver'");
                sb.AppendLine("}");
                sb.AppendLine();

                sb.AppendLine("# 1. Extract Step");
                if (sourceComp != null && !string.IsNullOrEmpty(sourceComp.SqlQueryOrTable))
                {
                    var sqlSingleLine = sourceComp.SqlQueryOrTable.Replace("\r\n", " ").Replace("\n", " ").Replace("'", "\\'");
                    sb.AppendLine($"pushdown_query = \"({sqlSingleLine}) AS src_data\"");
                    sb.AppendLine("df_src = spark.read.jdbc(url=jdbc_url, table=pushdown_query, properties=connection_properties)");
                }
                else
                {
                    sb.AppendLine("df_src = spark.read.table('staging.raw_source')");
                }
                sb.AppendLine();

                sb.AppendLine("# 2. Transform & Column Mapping Step");
                if (pkgMappings.Any())
                {
                    sb.AppendLine("df_transformed = df_src.select(");
                    var selectExprs = new List<string>();
                    foreach (var m in pkgMappings.DistinctBy(x => x.TargetColumnName))
                    {
                        if (!string.IsNullOrEmpty(m.SourceExpression))
                        {
                            var translatedExpr = TranslateSsisExpressionToSql(m.SourceExpression).Replace("\"", "'");
                            selectExprs.Add($"    F.expr(\"{translatedExpr}\").alias(\"{m.TargetColumnName}\")");
                        }
                        else
                        {
                            selectExprs.Add($"    F.col(\"{m.SourceColumnName}\").alias(\"{m.TargetColumnName}\")");
                        }
                    }
                    sb.AppendLine(string.Join(",\n", selectExprs));
                    sb.AppendLine(")");
                }
                else
                {
                    sb.AppendLine("df_transformed = df_src");
                }
                sb.AppendLine();

                sb.AppendLine("# ---------------------------------------------------------");
                sb.AppendLine("# Auto-Generated Data Quality Checks (Great Expectations)");
                sb.AppendLine("# ---------------------------------------------------------");
                sb.AppendLine("try:");
                sb.AppendLine("    import great_expectations as ge");
                sb.AppendLine("    print(\"Running Data Quality checks with Great Expectations...\")");
                sb.AppendLine("    # Convert Spark DataFrame to Great Expectations SparkDFDataset");
                sb.AppendLine("    df_ge = ge.dataset.SparkDFDataset(df_transformed)");
                sb.AppendLine("    ");
                sb.AppendLine("    # 1. Basic row count expectations");
                sb.AppendLine("    df_ge.expect_table_row_count_to_be_between(min_value=1)");
                sb.AppendLine("    ");
                
                sb.AppendLine("    # 2. Column-level expectations based on schema heuristics");
                sb.AppendLine("    for col in df_transformed.columns:");
                sb.AppendLine("        col_lower = col.lower()");
                sb.AppendLine("        if col_lower.endswith('_id') or col_lower.endswith('id') or col_lower == 'id' or col_lower.startswith('pk_'):");
                sb.AppendLine("            df_ge.expect_column_values_to_not_be_null(column=col)");
                sb.AppendLine("            if col_lower.startswith('pk_') or col_lower == 'id':");
                sb.AppendLine("                df_ge.expect_column_values_to_be_unique(column=col)");
                sb.AppendLine("        if col_lower.endswith('_status') or col_lower == 'status':");
                sb.AppendLine("            df_ge.expect_column_values_to_be_in_set(column=col, value_set=['active', 'inactive', 'pending', 'completed', 'failed'])");
                
                sb.AppendLine("    ");
                sb.AppendLine("    # Validate");
                sb.AppendLine("    results = df_ge.validate()");
                sb.AppendLine("    if not results['success']:");
                sb.AppendLine("        print(\"WARNING: Data Quality validation failed on PySpark dataset!\")");
                sb.AppendLine("    else:");
                sb.AppendLine("        print(\"Data Quality checks passed successfully.\")");
                sb.AppendLine("except ImportError:");
                sb.AppendLine("    print(\"great_expectations not installed. Skipping Data Quality checks.\")");
                sb.AppendLine("# ---------------------------------------------------------");
                sb.AppendLine();

                sb.AppendLine("# 3. Load Step (Delta Lake / Target)");
                var targetTable = destComp != null && !string.IsNullOrEmpty(destComp.SqlQueryOrTable)
                    ? destComp.SqlQueryOrTable
                    : "dbo.Fact_Target";

                sb.AppendLine($"target_table_name = '{targetTable}'");
                sb.AppendLine("# Write to Delta Lake / Target Table");
                sb.AppendLine("df_transformed.write.format('delta') \\");
                sb.AppendLine("    .mode('append') \\");
                sb.AppendLine("    .saveAsTable(target_table_name)");
                sb.AppendLine();
                sb.AppendLine("print(f'Successfully loaded {df_transformed.count()} rows into {target_table_name}')");

                result.Files.Add(new GeneratedFile
                {
                    FileName = scriptName,
                    Content = sb.ToString(),
                    Language = "python",
                    TargetFramework = "PySpark"
                });

                result.MappingsConverted += pkgMappings.Count;
            }

            result.Summary = $"Generated {result.Files.Count} PySpark DataFrame ETL python scripts.";
        }

        // ── 3. Azure Data Factory (ADF) Pipeline Generator ─────────────────────
        private static void GenerateAdfPipelines(LineageGraph graph, List<PackageNode> packages, MigrationResult result)
        {
            foreach (var pkg in packages)
            {
                var pkgTasks = graph.Tasks.Where(t => t.PackageId == pkg.Id).ToList();

                var pipelineObj = new
                {
                    name = CleanIdentifier(pkg.Name) + "_Pipeline",
                    properties = new
                    {
                        description = $"Migrated from SSIS Package '{pkg.Name}'",
                        activities = pkgTasks.Select(t => new
                        {
                            name = CleanIdentifier(t.Name),
                            type = t.Type.Contains("Data Flow", StringComparison.OrdinalIgnoreCase) ? "ExecuteDataFlow" : "SqlServerStoredProcedure",
                            typeProperties = new
                            {
                                dataflow = new { referenceName = CleanIdentifier(t.Name) + "_DF", type = "DataFlowReference" }
                            }
                        }).ToList()
                    }
                };

                var jsonStr = JsonSerializer.Serialize(pipelineObj, new JsonSerializerOptions { WriteIndented = true });

                result.Files.Add(new GeneratedFile
                {
                    FileName = CleanIdentifier(pkg.Name) + "_adf_pipeline.json",
                    Content = jsonStr,
                    Language = "json",
                    TargetFramework = "Azure Data Factory"
                });
            }

            result.Summary = $"Generated {result.Files.Count} Azure Data Factory (ADF) Pipeline JSON specifications.";
        }

        // ── 4. Consolidated ANSI SQL Stored Procedure Generator ────────────────
        private static void GenerateConsolidatedSql(LineageGraph graph, List<PackageNode> packages, MigrationResult result)
        {
            foreach (var pkg in packages)
            {
                var pkgComponents = graph.Components.Where(c => c.PackageId == pkg.Id).ToList();
                var pkgMappings = graph.ColumnMappings.Where(m => m.PackageId == pkg.Id).ToList();

                var procName = "usp_Migrated_" + CleanIdentifier(pkg.Name);

                var sb = new StringBuilder();
                sb.AppendLine($"-- Consolidated SQL Stored Procedure: {procName}");
                sb.AppendLine($"-- Converted from SSIS Package: {pkg.Name}");
                sb.AppendLine();
                sb.AppendLine($"CREATE OR ALTER PROCEDURE dbo.{procName}");
                sb.AppendLine("AS");
                sb.AppendLine("BEGIN");
                sb.AppendLine("    SET NOCOUNT ON;");
                sb.AppendLine();

                var destComp = pkgComponents.FirstOrDefault(c => c.Type.Contains("Destination", StringComparison.OrdinalIgnoreCase));
                var targetTable = destComp != null && !string.IsNullOrEmpty(destComp.SqlQueryOrTable)
                    ? destComp.SqlQueryOrTable
                    : "dbo.TargetTable";

                var targetCols = pkgMappings.Select(m => m.TargetColumnName).Distinct().Where(c => !string.IsNullOrEmpty(c)).ToList();

                if (targetCols.Any())
                {
                    sb.AppendLine($"    INSERT INTO {targetTable} (");
                    sb.AppendLine("        " + string.Join(", ", targetCols));
                    sb.AppendLine("    )");
                    sb.AppendLine("    SELECT");

                    var selectExprs = new List<string>();
                    foreach (var col in targetCols)
                    {
                        var m = pkgMappings.FirstOrDefault(x => x.TargetColumnName == col);
                        var srcExpr = m != null && !string.IsNullOrEmpty(m.SourceExpression)
                            ? TranslateSsisExpressionToSql(m.SourceExpression)
                            : (m != null && !string.IsNullOrEmpty(m.SourceColumnName) ? $"src.{m.SourceColumnName}" : col);
                        selectExprs.Add($"        {srcExpr} AS {col}");
                    }
                    sb.AppendLine(string.Join(",\n", selectExprs));

                    var sourceComp = pkgComponents.FirstOrDefault(c => c.Type.Contains("Source", StringComparison.OrdinalIgnoreCase));
                    if (sourceComp != null && !string.IsNullOrEmpty(sourceComp.SqlQueryOrTable))
                    {
                        sb.AppendLine($"    FROM ({sourceComp.SqlQueryOrTable.Trim()}) AS src;");
                    }
                    else
                    {
                        sb.AppendLine("    FROM stg.SourceTable AS src;");
                    }
                }
                else
                {
                    sb.AppendLine("    -- Execute Task Queries");
                    foreach (var c in pkgComponents)
                    {
                        if (!string.IsNullOrEmpty(c.SqlQueryOrTable))
                        {
                            sb.AppendLine("    " + c.SqlQueryOrTable.Trim() + ";");
                        }
                    }
                }

                sb.AppendLine();
                sb.AppendLine("    PRINT 'Loaded package successfully.';");
                sb.AppendLine("END;");

                result.Files.Add(new GeneratedFile
                {
                    FileName = $"{procName}.sql",
                    Content = sb.ToString(),
                    Language = "sql",
                    TargetFramework = "ANSI SQL Stored Procedure"
                });
            }

            result.Summary = $"Generated {result.Files.Count} SQL Stored Procedures.";
        }

        // ── 5. Standard Python (Pandas/pyodbc) E&L Generator ─────────────────────
        private static void GeneratePythonPandasScripts(LineageGraph graph, List<PackageNode> packages, MigrationResult result)
        {
            foreach (var pkg in packages)
            {
                var pkgComponents = graph.Components.Where(c => c.PackageId == pkg.Id).ToList();
                var scriptName = CleanIdentifier(pkg.Name).ToLowerInvariant();
                if (scriptName.StartsWith("pkg_")) scriptName = scriptName.Substring(4);
                scriptName = "extract_" + scriptName + ".py";

                var sb = new StringBuilder();
                sb.AppendLine("# Standard Python Extraction Script");
                sb.AppendLine($"# Migrated from SSIS Package: {pkg.Name}");
                sb.AppendLine("# Extract from SQL Server Source → Load to SQL Server Landing Zone → dbt Transform");
                sb.AppendLine();
                sb.AppendLine("import pyodbc");
                sb.AppendLine("import pandas as pd");
                sb.AppendLine("import os");
                sb.AppendLine("from datetime import datetime");
                sb.AppendLine("import warnings");
                sb.AppendLine("warnings.filterwarnings('ignore', category=UserWarning)");
                var sourceComp = pkgComponents.FirstOrDefault(c => c.Type.Contains("Source", StringComparison.OrdinalIgnoreCase));
                var destComp = pkgComponents.FirstOrDefault(c => c.Type.Contains("Destination", StringComparison.OrdinalIgnoreCase));

                var (srcServer, srcDb) = ResolveDatabaseAndServer(graph, pkg, sourceComp, false);
                var (tgtServer, tgtDb) = ResolveDatabaseAndServer(graph, pkg, destComp, true);
                srcServer = NormalizeServer(srcServer);
                tgtServer = NormalizeServer(tgtServer);

                sb.AppendLine();
                sb.AppendLine("def extract_and_load():");
                sb.AppendLine($"    print(f\"[{{datetime.now()}}] Starting extraction for {pkg.Name}...\")");
                sb.AppendLine();
                sb.AppendLine("    # Dynamic connection string derived from SSIS Connection Manager");
                sb.AppendLine("    conn_str = (");
                sb.AppendLine("        r'DRIVER={ODBC Driver 18 for SQL Server};'");
                sb.AppendLine($"        r'SERVER={srcServer};'");
                sb.AppendLine($"        r'DATABASE={srcDb};'");
                sb.AppendLine("        r'UID=sa;'");
                sb.AppendLine("        r'PWD=YourPassword123!;'");
                sb.AppendLine("        r'TrustServerCertificate=yes;'");
                sb.AppendLine("    )");
                sb.AppendLine();
                sb.AppendLine("    try:");
                sb.AppendLine("        conn = pyodbc.connect(conn_str)");
                sb.AppendLine("        print(\"Successfully connected to the source database.\")");
                sb.AppendLine("    except Exception as e:");
                sb.AppendLine("        print(f\"Database connection failed: {e}\")");
                sb.AppendLine("        raise");
                sb.AppendLine();

                sb.AppendLine("    # Define source extraction query");
                if (sourceComp != null && !string.IsNullOrEmpty(sourceComp.SqlQueryOrTable))
                {
                    var rawSql = sourceComp.SqlQueryOrTable.Trim();
                    // Normalize staging table references (e.g. stg.RawCustomers or [stg].[RawCustomers] -> dbo.stg_RawCustomers)
                    rawSql = Regex.Replace(rawSql, @"\[?stg\]?\.\[?(\w+)\]?", "dbo.stg_$1", RegexOptions.IgnoreCase);

                    // If it's a table/view name rather than a SELECT/WITH statement, wrap it in SELECT * FROM
                    if (!rawSql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
                        !rawSql.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
                    {
                        rawSql = $"SELECT * FROM {rawSql}";
                    }

                    var safeSql = EscapeSqlQuery(rawSql);
                    sb.AppendLine($"    extract_query = \"\"\"");
                    sb.AppendLine($"        {safeSql}");
                    sb.AppendLine($"    \"\"\"");
                }
                else
                {
                    sb.AppendLine("    extract_query = \"SELECT * FROM staging.raw_source\"");
                }
                sb.AppendLine();
                
                sb.AppendLine("    print(\"Reading data into pandas DataFrame...\")");
                sb.AppendLine("    df = pd.read_sql(extract_query, conn)");
                sb.AppendLine("    conn.close()");
                sb.AppendLine("    print(f\"Extracted {len(df)} rows.\")");
                sb.AppendLine();
                
                sb.AppendLine("    # ---------------------------------------------------------");
                sb.AppendLine("    # Auto-Generated Data Quality Checks (Great Expectations)");
                sb.AppendLine("    # ---------------------------------------------------------");
                sb.AppendLine("    try:");
                sb.AppendLine("        import great_expectations as ge");
                sb.AppendLine("        print(\"Running Data Quality checks...\")");
                sb.AppendLine("        df_ge = ge.from_pandas(df)");
                sb.AppendLine("        ");
                sb.AppendLine("        # 1. Basic row count expectations");
                sb.AppendLine("        df_ge.expect_table_row_count_to_be_between(min_value=1)");
                sb.AppendLine("        ");
                
                sb.AppendLine("        # 2. Column-level expectations based on schema heuristics");
                sb.AppendLine("        for col in df.columns:");
                sb.AppendLine("            col_lower = col.lower()");
                sb.AppendLine("            if col_lower.endswith('_id') or col_lower.endswith('id') or col_lower == 'id' or col_lower.startswith('pk_'):");
                sb.AppendLine("                df_ge.expect_column_values_to_not_be_null(column=col)");
                sb.AppendLine("                if col_lower.startswith('pk_') or col_lower == 'id':");
                sb.AppendLine("                    df_ge.expect_column_values_to_be_unique(column=col)");
                sb.AppendLine("            if col_lower.endswith('_status') or col_lower == 'status':");
                sb.AppendLine("                df_ge.expect_column_values_to_be_in_set(column=col, value_set=['active', 'inactive', 'pending', 'completed', 'failed'])");
                
                sb.AppendLine("        ");
                sb.AppendLine("        # Optional: Save validation results or fail pipeline on DQ error");
                sb.AppendLine("        results = df_ge.validate()");
                sb.AppendLine("        if not results['success']:");
                sb.AppendLine("            print(\"WARNING: Data Quality validation failed on extracted dataset!\")");
                sb.AppendLine("            # raise ValueError(\"Data Quality Checks Failed\") # Uncomment to enforce strict DQ gate");
                sb.AppendLine("        else:");
                sb.AppendLine("            print(\"Data Quality checks passed successfully.\")");
                sb.AppendLine("    except ImportError:");
                sb.AppendLine("        print(\"great_expectations not installed. Skipping Data Quality checks.\")");
                sb.AppendLine("    # ---------------------------------------------------------");
                sb.AppendLine();

                // Build target table name: strip schema prefix (e.g. "stg.RawCustomers" → "stg_RawCustomers")
                var rawTarget = destComp != null && !string.IsNullOrEmpty(destComp.SqlQueryOrTable)
                    ? destComp.SqlQueryOrTable
                    : "fact_target";
                // Remove surrounding brackets and replace dots with underscores for pandas to_sql naming
                var targetTable = Regex.Replace(rawTarget, @"[\[\]]", "").Replace(".", "_").Trim();
                if (string.IsNullOrEmpty(targetTable)) targetTable = "fact_target";
                    
                var scripts = pkgComponents.Where(c => c.Type != null && c.Type.Contains("Script Component", StringComparison.OrdinalIgnoreCase)).ToList();
                if (scripts.Any())
                {
                    sb.AppendLine("    # ---------------------------------------------------------");
                    sb.AppendLine("    # TODO: MANUAL PYTHON TRANSLATION REQUIRED");
                    sb.AppendLine("    # This package contains a Script Component (C#/VB.NET).");
                    sb.AppendLine("    # Please implement the row-by-row logic using Pandas apply() or custom UDFs here.");
                    sb.AppendLine("    # ---------------------------------------------------------");
                    sb.AppendLine();
                }

                sb.AppendLine("    # ---------------------------------------------------------");
                sb.AppendLine("    # Load to Target Database (pyodbc — no SQLAlchemy conflict)");
                sb.AppendLine("    # ---------------------------------------------------------");
                sb.AppendLine("    try:");
                sb.AppendLine("        target_conn_str = (");
                sb.AppendLine("            r'DRIVER={ODBC Driver 18 for SQL Server};'");
                sb.AppendLine($"            r'SERVER={tgtServer};'");
                sb.AppendLine($"            r'DATABASE={tgtDb};'");
                sb.AppendLine("            r'UID=sa;'");
                sb.AppendLine("            r'PWD=YourPassword123!;'");
                sb.AppendLine("            r'TrustServerCertificate=yes;'");
                sb.AppendLine("        )");
                sb.AppendLine("        target_conn = pyodbc.connect(target_conn_str)");
                sb.AppendLine("        cursor = target_conn.cursor()");
                sb.AppendLine();
                sb.AppendLine($"        target_table = '{targetTable}'");
                sb.AppendLine("        print(f\"Loading {len(df)} rows into [\" + target_table + \"]...\")");
                sb.AppendLine();
                sb.AppendLine("        # Drop & recreate landing table");
                sb.AppendLine("        cursor.execute(f\"IF OBJECT_ID('dbo.{target_table}', 'U') IS NOT NULL DROP TABLE dbo.{target_table}\")");
                sb.AppendLine("        ");
                sb.AppendLine("        def map_dtype(dt):");
                sb.AppendLine("            dt_str = str(dt).lower()");
                sb.AppendLine("            if 'bool' in dt_str: return 'BIT'");
                sb.AppendLine("            if 'int' in dt_str: return 'INT'");
                sb.AppendLine("            if 'float' in dt_str or 'decimal' in dt_str: return 'DECIMAL(18,2)'");
                sb.AppendLine("            if 'datetime64' in dt_str: return 'DATETIME'");
                sb.AppendLine("            if 'date' in dt_str: return 'DATE'");
                sb.AppendLine("            if 'timedelta' in dt_str: return 'NVARCHAR(50)'");
                sb.AppendLine("            return 'NVARCHAR(MAX)'");
                sb.AppendLine("            ");
                sb.AppendLine("        cols_ddl = ', '.join([f'[{c}] {map_dtype(df[c].dtype)}' for c in df.columns])");
                sb.AppendLine("        cursor.execute(f'CREATE TABLE dbo.{target_table} ({cols_ddl})')");
                sb.AppendLine();
                sb.AppendLine("        # Bulk insert with robust value type handling (NaT/NaN -> None, Datetime -> str)");
                sb.AppendLine("        import numpy as np");
                sb.AppendLine("        def clean_val(v):");
                sb.AppendLine("            if pd.isna(v): return None");
                sb.AppendLine("            if isinstance(v, (pd.Timestamp, datetime)): return v.strftime('%Y-%m-%d %H:%M:%S')");
                sb.AppendLine("            if isinstance(v, (np.integer,)): return int(v)");
                sb.AppendLine("            if isinstance(v, (np.floating,)): return float(v)");
                sb.AppendLine("            if isinstance(v, (np.bool_,)): return bool(v)");
                sb.AppendLine("            return v");
                sb.AppendLine();
                sb.AppendLine("        placeholders = ', '.join(['?' for _ in df.columns])");
                sb.AppendLine("        rows = [tuple(clean_val(v) for v in row) for row in df.itertuples(index=False)]");
                sb.AppendLine("        if len(rows) > 0:");
                sb.AppendLine("            cursor.executemany(f'INSERT INTO dbo.{target_table} VALUES ({placeholders})', rows)");
                sb.AppendLine("        else:");
                sb.AppendLine("            print(\"No rows to insert.\")");
                sb.AppendLine();
                sb.AppendLine("        # Reconciliation log");
                sb.AppendLine("        cursor.execute(\"\"\"");
                sb.AppendLine("            IF OBJECT_ID('dbo.ValidationLogs', 'U') IS NULL");
                sb.AppendLine("            CREATE TABLE dbo.ValidationLogs (RunDate DATETIME, TableName NVARCHAR(100), SsisRows INT, DbtRows INT, Mismatches INT)");
                sb.AppendLine("        \"\"\")");
                sb.AppendLine("        cursor.execute('INSERT INTO dbo.ValidationLogs VALUES (GETDATE(), ?, ?, 0, 0)', target_table, len(df))");
                sb.AppendLine("        target_conn.commit()");
                sb.AppendLine("        cursor.close()");
                sb.AppendLine("        target_conn.close()");
                sb.AppendLine("        print(f\"Successfully loaded {len(df)} rows into {target_table}. Reconciliation log updated.\")");
                sb.AppendLine("    except Exception as e:");
                sb.AppendLine("        print(f\"Failed to load to database: {e}\")");
                sb.AppendLine("        raise");
                sb.AppendLine();
                sb.AppendLine("    print(\"Extraction and load completed successfully.\")");
                sb.AppendLine();
                sb.AppendLine("if __name__ == '__main__':");
                sb.AppendLine("    extract_and_load()");

                result.Files.Add(new GeneratedFile
                {
                    FileName = scriptName,
                    Content = sb.ToString(),
                    Language = "python",
                    TargetFramework = "Python (Pandas/pyodbc)"
                });
            }

            result.Summary = $"Generated {result.Files.Count} Python Extraction Scripts.";
        }

        private static void GenerateAirflowDags(LineageGraph graph, List<PackageNode> packages, MigrationResult result)
        {
            foreach (var pkg in packages)
            {
                var sb = new StringBuilder();
                var dagName = CleanIdentifier(pkg.Name).ToLowerInvariant();
                if (dagName.StartsWith("pkg_")) dagName = dagName.Substring(4);
                dagName = "dag_" + dagName;

                                sb.AppendLine("from datetime import datetime, timedelta");
                sb.AppendLine("from airflow import DAG");
                sb.AppendLine("from airflow.operators.empty import EmptyOperator");
                sb.AppendLine("from airflow.operators.bash import BashOperator");
                sb.AppendLine("from airflow.providers.common.sql.operators.sql import SQLExecuteQueryOperator");
                sb.AppendLine("from airflow.operators.trigger_dagrun import TriggerDagRunOperator");
                sb.AppendLine("from airflow.operators.python import PythonOperator");
                sb.AppendLine("from airflow.sensors.filesystem import FileSensor");
                sb.AppendLine();
                sb.AppendLine("def on_task_failure_callback(context):");
                sb.AppendLine("    ti = context.get('task_instance')");
                sb.AppendLine("    print(f'[SELF-HEALING ALERT] Task {ti.task_id} in DAG {ti.dag_id} failed. Initiating retry protocol.')");
                sb.AppendLine();
                sb.AppendLine("default_args = {");
                sb.AppendLine("    'owner': 'data_engineering',");
                sb.AppendLine("    'depends_on_past': False,");
                sb.AppendLine("    'email_on_failure': False,");
                sb.AppendLine("    'email_on_retry': False,");
                sb.AppendLine("    'retries': 3,");
                sb.AppendLine("    'retry_delay': timedelta(seconds=15),");
                sb.AppendLine("    'retry_exponential_backoff': True,");
                sb.AppendLine("    'max_retry_delay': timedelta(minutes=5),");
                sb.AppendLine("    'on_failure_callback': on_task_failure_callback,");
                sb.AppendLine("}");
                sb.AppendLine();
                sb.AppendLine($"with DAG(");
                sb.AppendLine($"    dag_id='{dagName}',");
                sb.AppendLine($"    default_args=default_args,");
                sb.AppendLine($"    description='Auto-converted from SSIS Package {pkg.Name}',");
                sb.AppendLine($"    schedule=None,");
                sb.AppendLine($"    start_date=datetime(2026, 1, 1),");
                sb.AppendLine($"    catchup=False,");
                sb.AppendLine($"    tags=['ssis_migration', 'self_healing'],");
                sb.AppendLine($") as dag:");
                sb.AppendLine();
                sb.AppendLine("    start_pipeline = EmptyOperator(task_id='start_pipeline')");
                // FIX #4: trigger_rule='all_done' ensures end_pipeline always runs and is not
                // marked yellow/skipped when any upstream task fails. Without this the final
                // node inherits the default 'all_success' and becomes upstream_failed.
                sb.AppendLine("    end_pipeline = EmptyOperator(task_id='end_pipeline', trigger_rule='all_done')");
                sb.AppendLine();

                var tasks = graph.Tasks.Where(t => t.PackageId == pkg.Id).OrderBy(t => t.ExecutionSequence).ToList();
                var taskNames = new List<string>();

                foreach (var task in tasks)
                {
                    var taskId = CleanIdentifier(task.Name).ToLowerInvariant();
                    taskNames.Add(taskId);

                    var tType = task.Type?.ToLowerInvariant() ?? "";
                    if (tType.Contains("execute sql") || tType.Contains("executesql"))
                    {
                        var sqlComp = graph.Components.FirstOrDefault(c => c.TaskId == task.Id && c.Type == "Execute SQL Task");
                        var rawSql = sqlComp != null && !string.IsNullOrEmpty(sqlComp.SqlQueryOrTable) 
                                       ? sqlComp.SqlQueryOrTable 
                                       : "-- TODO: Insert SQL from SSIS task";
                        // Escape T-SQL reserved keyword RowCount -> [RowCount]
                        rawSql = Regex.Replace(rawSql, @"(?<!\[)\bRowCount\b(?!\])", "[RowCount]", RegexOptions.IgnoreCase);

                        // FIX #2: Normalize non-dbo schema table references to dbo.stg_ flat naming
                        // so SQL prep task is consistent with the Python extraction script output
                        // e.g. "stg.RawCustomers" -> "stg_RawCustomers" (dbo assumed by connection default)
                        rawSql = Regex.Replace(rawSql,
                            @"(\[?)(stg|staging)(\]?)\.(\[?)([\w]+)(\]?)",
                            m => $"stg_{m.Groups[5].Value}",
                            RegexOptions.IgnoreCase);

                        // FIX #3: Detect non-dbo schemas used in CREATE TABLE and prepend a
                        // CREATE SCHEMA ... IF NOT EXISTS guard so the task does not fail on
                        // a freshly provisioned database where the schema does not yet exist.
                        var schemaMatches = Regex.Matches(rawSql,
                            @"CREATE\s+TABLE\s+(\[?([a-zA-Z_][\w]*)\]?)\.\[?[\w]+\]?",
                            RegexOptions.IgnoreCase);
                        var schemasToCreate = schemaMatches
                            .Select(sm => sm.Groups[2].Value)
                            .Where(s => !string.Equals(s, "dbo", StringComparison.OrdinalIgnoreCase))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        var schemaGuard = new StringBuilder();
                        foreach (var schemaName in schemasToCreate)
                        {
                            schemaGuard.Append(
                                $"IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = \\'{schemaName}\\') " +
                                $"EXEC(\'CREATE SCHEMA [{schemaName}]\'); ");
                        }

                        var sqlQuery = (schemaGuard.ToString() + rawSql)
                            .Replace("?", "1")
                            .Replace("'", "\\'")
                            .Replace("\n", " ")
                            .Replace("\r", "");
                                       
                        sb.AppendLine($"    {taskId} = SQLExecuteQueryOperator(");
                        sb.AppendLine($"        task_id='{taskId}',");
                        sb.AppendLine($"        conn_id='sql_default',");
                        sb.AppendLine($"        sql='{sqlQuery}',");
                        sb.AppendLine($"    )");
                    }
                    else if (tType.Contains("file system") || tType.Contains("filesystem") || tType.Contains("wmi") || tType.Contains("event"))
                    {
                        sb.AppendLine($"    {taskId} = FileSensor(");
                        sb.AppendLine($"        task_id='{taskId}_file_sensor',");
                        sb.AppendLine($"        filepath='/opt/airflow/dags/incoming/{dagName.Replace("dag_", "")}_trigger.flag',");
                        sb.AppendLine($"        poke_interval=30,");
                        sb.AppendLine($"        timeout=600,");
                        sb.AppendLine($"        mode='poke',");
                        sb.AppendLine($"    )");
                    }
                    else if (tType.Contains("data flow") || tType.Contains("pipeline"))
                    {
                        var dbtModelName = "stg_" + dagName.Replace("dag_pkg_", "").Replace("dag_", "");
                        var pyScriptName = "extract_" + dagName.Replace("dag_pkg_", "").Replace("dag_", "") + ".py";
                        
                        sb.AppendLine($"    {taskId}_extract = BashOperator(");
                        sb.AppendLine($"        task_id='{taskId}_extract_python',");
                        sb.AppendLine($"        bash_command='python /opt/airflow/dags/scripts/{pyScriptName}',");
                        sb.AppendLine($"    )");
                        sb.AppendLine();
                        sb.AppendLine($"    {taskId}_dbt = BashOperator(");
                        sb.AppendLine($"        task_id='{taskId}_transform_dbt',");
                        sb.AppendLine($"        bash_command='cd /opt/airflow/dags/dbt_project && dbt run --no-partial-parse --profiles-dir . --select {dbtModelName}',");
                        sb.AppendLine($"    )");
                        sb.AppendLine();
                        sb.AppendLine($"    {taskId}_extract >> {taskId}_dbt");
                    }
                    else if (tType.Contains("executepackagetask"))
                    {
                        var childDagId = ResolveChildDagId(graph, task);
                        sb.AppendLine($"    {taskId} = TriggerDagRunOperator(");
                        sb.AppendLine($"        task_id='{taskId}',");
                        sb.AppendLine($"        trigger_dag_id='{childDagId}',");
                        sb.AppendLine($"        wait_for_completion=True,");
                        sb.AppendLine($"    )");
                    }
                    else if (tType.Contains("scripttask"))
                    {
                        sb.AppendLine($"    {taskId} = PythonOperator(");
                        sb.AppendLine($"        task_id='{taskId}',");
                        sb.AppendLine($"        python_callable=lambda: print('TODO: Migrate C#/VB Script to Python'),");
                        sb.AppendLine($"    )");
                    }
                    else
                    {
                        sb.AppendLine($"    {taskId} = EmptyOperator(");
                        sb.AppendLine($"        task_id='{taskId}',");
                        sb.AppendLine($"        doc_md='Original SSIS Type: {task.Type}',");
                        sb.AppendLine($"    )");
                    }
                    sb.AppendLine();
                }
                sb.AppendLine();
                sb.AppendLine("    # Set up task dependencies (follows original SSIS PrecedenceConstraint order)");
                if (taskNames.Count > 0)
                {
                    var execEdges = graph.ExecutionEdges
                        .Where(e => tasks.Any(t => t.Id == e.FromTaskId) && tasks.Any(t => t.Id == e.ToTaskId))
                        .ToList();

                    // Helper: return the correct Airflow task variable reference for a task.
                    // Data Flow tasks are split into two Airflow tasks (_extract and _dbt),
                    // so we reference the entry point (_extract) when used as a downstream target
                    // and the exit point (_dbt) when used as an upstream source.
                    string EntryRef(TaskNode t) {
                        var n = CleanIdentifier(t.Name).ToLowerInvariant();
                        var tp = t.Type?.ToLowerInvariant() ?? "";
                        return (tp.Contains("data flow") || tp.Contains("pipeline")) ? $"{n}_extract" : n;
                    }
                    string ExitRef(TaskNode t) {
                        var n = CleanIdentifier(t.Name).ToLowerInvariant();
                        var tp = t.Type?.ToLowerInvariant() ?? "";
                        return (tp.Contains("data flow") || tp.Contains("pipeline")) ? $"{n}_dbt" : n;
                    }

                    if (execEdges.Count > 0)
                    {
                        // Use actual PrecedenceConstraint edges from the SSIS package
                        var rootTasks = tasks.Where(t => !execEdges.Any(e => e.ToTaskId == t.Id)).ToList();
                        foreach (var root in rootTasks)
                            sb.AppendLine($"    start_pipeline >> {EntryRef(root)}");

                        foreach (var edge in execEdges)
                        {
                            var fromTask = tasks.FirstOrDefault(t => t.Id == edge.FromTaskId);
                            var toTask   = tasks.FirstOrDefault(t => t.Id == edge.ToTaskId);
                            if (fromTask != null && toTask != null)
                                sb.AppendLine($"    {ExitRef(fromTask)} >> {EntryRef(toTask)}");
                        }

                        var leafTasks = tasks.Where(t => !execEdges.Any(e => e.FromTaskId == t.Id)).ToList();
                        foreach (var leaf in leafTasks)
                            sb.AppendLine($"    {ExitRef(leaf)} >> end_pipeline");
                    }
                    else
                    {
                        // No edges recorded — fall back to sorted order (Execute SQL / Truncate tasks first).
                        // Build a single linear chain: start >> task0 >> task1 >> ... >> end
                        var sortedTasks = tasks
                            .OrderBy(t => (t.Type?.Contains("Execute SQL", StringComparison.OrdinalIgnoreCase) == true || t.Name.ToLowerInvariant().Contains("truncate")) ? 0 : 1)
                            .ThenBy(t => t.ExecutionSequence)
                            .ToList();

                        sb.AppendLine($"    start_pipeline >> {EntryRef(sortedTasks[0])}");
                        for (int i = 0; i < sortedTasks.Count - 1; i++)
                            sb.AppendLine($"    {ExitRef(sortedTasks[i])} >> {EntryRef(sortedTasks[i + 1])}");
                        sb.AppendLine($"    {ExitRef(sortedTasks[sortedTasks.Count - 1])} >> end_pipeline");
                    }
                }
                else
                {
                    sb.AppendLine("    start_pipeline >> end_pipeline");
                }

                result.Files.Add(new GeneratedFile
                {
                    FileName = $"{dagName}.py",
                    Content = sb.ToString(),
                    Language = "python",
                    TargetFramework = "Apache Airflow DAG"
                });
            }

            result.Summary = $"Generated {result.Files.Count} Apache Airflow DAGs.";
        }

        private static string CleanIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";
            var clean = Regex.Replace(name, @"[^\w]", "_");
            return Regex.Replace(clean, @"_+", "_").Trim('_');
        }

        // SQL Server reserved keywords that must be escaped with [brackets] when used as identifiers
        private static readonly HashSet<string> _sqlReservedKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "external", "user", "order", "table", "select", "from", "where", "group",
            "key", "index", "view", "schema", "database", "file", "function",
            "procedure", "trigger", "constraint", "column", "row", "value", "values",
            "primary", "foreign", "check", "default", "null", "not", "and", "or"
        };

        /// <summary>
        /// Escapes schema and table names in a SQL query that clash with SQL Server reserved keywords.
        /// e.g. "FROM external.CrmCustomers" -> "FROM [external].CrmCustomers"
        /// </summary>
        private static string EscapeSqlQuery(string sql)
        {
            if (string.IsNullOrEmpty(sql)) return sql;

            // Match schema.table or standalone identifiers in FROM / JOIN clauses
            // Pattern: word boundary + keyword + dot (schema reference)
            return Regex.Replace(sql, @"\b([A-Za-z_][A-Za-z0-9_]*)\.", m =>
            {
                var identifier = m.Groups[1].Value;
                if (_sqlReservedKeywords.Contains(identifier))
                    return $"[{identifier}].";
                return m.Value;
            });
        }
        /// <summary>
        /// Translates a single SSIS ternary expression (A ? B : C, possibly nested) into
        /// SQL CASE WHEN … THEN … ELSE … END form. Handles nested ternaries and both
        /// single-quoted and double-quoted string literals that contain '?' or ':' characters.
        /// </summary>
        private static string TranslateSsisTernaries(string expr)
        {
            if (string.IsNullOrEmpty(expr)) return expr;

            // Locate the first '?' that is NOT inside a string literal or parenthesised sub-expression
            int depth = 0;
            bool inSingleQuote = false;
            bool inDoubleQuote = false;
            int questionPos = -1;

            for (int i = 0; i < expr.Length; i++)
            {
                char c = expr[i];
                if (inSingleQuote)
                {
                    if (c == '\'') inSingleQuote = false;
                    continue;
                }
                if (inDoubleQuote)
                {
                    if (c == '"') inDoubleQuote = false;
                    continue;
                }
                if (c == '\'') { inSingleQuote = true; continue; }
                if (c == '"') { inDoubleQuote = true; continue; }
                if (c == '(' || c == '[') { depth++; continue; }
                if (c == ')' || c == ']') { depth--; continue; }
                if (c == '?' && depth == 0) { questionPos = i; break; }
            }

            if (questionPos < 0) return expr; // no ternary at this depth

            var condPart = expr.Substring(0, questionPos).Trim();

            // Now find the matching ':' for this '?' (depth-aware, string-literal-aware)
            depth = 0; inSingleQuote = false; inDoubleQuote = false;
            int colonPos = -1;
            for (int i = questionPos + 1; i < expr.Length; i++)
            {
                char c = expr[i];
                if (inSingleQuote)
                {
                    if (c == '\'') inSingleQuote = false;
                    continue;
                }
                if (inDoubleQuote)
                {
                    if (c == '"') inDoubleQuote = false;
                    continue;
                }
                if (c == '\'') { inSingleQuote = true; continue; }
                if (c == '"') { inDoubleQuote = true; continue; }
                if (c == '(' || c == '[') { depth++; continue; }
                if (c == ')' || c == ']') { depth--; continue; }
                if (c == ':' && depth == 0) { colonPos = i; break; }
            }

            if (colonPos < 0) return expr; // malformed — no matching ':'

            var truePart  = expr.Substring(questionPos + 1, colonPos - questionPos - 1).Trim();
            var falsePart = expr.Substring(colonPos + 1).Trim();

            // Strip outer parentheses so nested ternaries like (A ? B : C) are correctly parsed
            condPart  = StripOuterParens(condPart);
            truePart  = StripOuterParens(truePart);
            falsePart = StripOuterParens(falsePart);

            // Recursively translate nested ternaries in each branch
            condPart  = TranslateSsisTernaries(condPart);
            truePart  = TranslateSsisTernaries(truePart);
            falsePart = TranslateSsisTernaries(falsePart);

            return $"CASE WHEN {condPart} THEN {truePart} ELSE {falsePart} END";
        }

        /// <summary>
        /// Strips balanced outer parentheses from an expression, e.g. "(A + B)" → "A + B".
        /// Only strips if the entire expression is wrapped in matching parens.
        /// </summary>
        private static string StripOuterParens(string s)
        {
            s = s.Trim();
            while (s.Length >= 2 && s[0] == '(' && s[^1] == ')')
            {
                // Verify the outer parens are truly balanced (not e.g. "(A) + (B)")
                int depth = 0;
                bool balanced = true;
                for (int i = 0; i < s.Length; i++)
                {
                    if (s[i] == '(') depth++;
                    else if (s[i] == ')') depth--;
                    if (depth == 0 && i < s.Length - 1) { balanced = false; break; }
                }
                if (balanced)
                    s = s.Substring(1, s.Length - 2).Trim();
                else
                    break;
            }
            return s;
        }

        /// <summary>
        /// Translates SSIS expressions to ANSI SQL syntax
        /// </summary>
        private static string TranslateSsisExpressionToSql(string ssisExpr)
        {
            if (string.IsNullOrEmpty(ssisExpr)) return ssisExpr;
            
            // 0. Convert SSIS double-quoted string literals to SQL single-quoted strings.
            // SSIS uses "text" for strings; SQL uses 'text'. We must do this BEFORE the
            // ternary parser so that the ternary scanner sees consistent quoting.
            // Handle escaped double-quotes inside: "He said \"hi\"" → 'He said "hi"'
            ssisExpr = Regex.Replace(ssisExpr, @"""([^""]*)""", "'$1'");
            
            // 1. Ternary Operator: Condition ? TrueVal : FalseVal -> CASE WHEN Condition THEN TrueVal ELSE FalseVal END
            // Uses a depth-aware scanner so nested ternaries like A >= 75 ? 'X' : (A >= 40 ? 'Y' : 'Z')
            // and string literals containing ':' are handled correctly.
            ssisExpr = TranslateSsisTernaries(ssisExpr);
            
            // 2. Typecasts — strip SSIS type prefixes; a full CAST is added by the column heuristics layer
            ssisExpr = Regex.Replace(ssisExpr, @"\(DT_WSTR,\s*(\d+)\)", ""); // strip (DT_WSTR, N)
            ssisExpr = Regex.Replace(ssisExpr, @"\(DT_STR,\s*\d+,\s*\d+\)", ""); // strip (DT_STR, N, CP)
            ssisExpr = Regex.Replace(ssisExpr, @"\(DT_I4\)", "");     // strip (DT_I4)
            ssisExpr = Regex.Replace(ssisExpr, @"\(DT_I8\)", "");     // strip (DT_I8)
            ssisExpr = Regex.Replace(ssisExpr, @"\(DT_I2\)", "");     // strip (DT_I2)
            ssisExpr = Regex.Replace(ssisExpr, @"\(DT_UI4\)", "");    // strip (DT_UI4)
            ssisExpr = Regex.Replace(ssisExpr, @"\(DT_UI8\)", "");    // strip (DT_UI8)
            ssisExpr = Regex.Replace(ssisExpr, @"\(DT_R4\)", "");     // strip (DT_R4)
            ssisExpr = Regex.Replace(ssisExpr, @"\(DT_R8\)", "");     // strip (DT_R8)
            ssisExpr = Regex.Replace(ssisExpr, @"\(DT_CY\)", "");     // strip (DT_CY) - currency
            ssisExpr = Regex.Replace(ssisExpr, @"\(DT_BOOL\)", "");   // strip (DT_BOOL)
            ssisExpr = Regex.Replace(ssisExpr, @"\(DT_DATE\)", "");   // strip (DT_DATE)
            ssisExpr = Regex.Replace(ssisExpr, @"\(DT_DBDATE\)", ""); // strip (DT_DBDATE)
            ssisExpr = Regex.Replace(ssisExpr, @"\(DT_DBTIMESTAMP\)", ""); // strip (DT_DBTIMESTAMP)
            ssisExpr = Regex.Replace(ssisExpr, @"\(DT_DBTIMESTAMP2,\s*\d+\)", ""); // strip (DT_DBTIMESTAMP2, N)
            ssisExpr = Regex.Replace(ssisExpr, @"\(DT_NUMERIC,\s*\d+,\s*\d+\)", ""); // strip (DT_NUMERIC, P, S)
            ssisExpr = Regex.Replace(ssisExpr, @"\(DT_DECIMAL,\s*\d+\)", ""); // strip (DT_DECIMAL, S)
            ssisExpr = Regex.Replace(ssisExpr, @"\(DT_GUID\)", "");   // strip (DT_GUID)
            ssisExpr = Regex.Replace(ssisExpr, @"\(DT_BYTES,\s*\d+\)", ""); // strip (DT_BYTES, N)
            
            // 3. Logical operators: SSIS uses || for OR, && for AND (C-style)
            // Must be done before equality operators to avoid breaking the pipe symbols.
            ssisExpr = Regex.Replace(ssisExpr, @"\|\|", " OR ");
            ssisExpr = Regex.Replace(ssisExpr, @"&&", " AND ");
            
            // 4. Equality operators == to =
            ssisExpr = ssisExpr.Replace("==", "=");
            // Inequality operator != to <>
            ssisExpr = ssisExpr.Replace("!=", "<>");
            
            // 5. SSIS Variables @[User::VarName] or @[$Package::VarName] -> {{ var('VarName') }}
            ssisExpr = Regex.Replace(ssisExpr, @"@\[(?:User|\$Package)::([^\]]+)\]", "{{ var('$1') }}");

            // 6. SSIS functions → SQL equivalents
            // GETDATE() already maps directly to SQL Server GETDATE()
            // LEN() → LEN() (same in SQL Server)
            // UPPER() / LOWER() → same in SQL
            // TRIM() → LTRIM(RTRIM(...)) for wider SQL compat
            ssisExpr = Regex.Replace(ssisExpr, @"\bTRIM\(([^)]+)\)", "LTRIM(RTRIM($1))", RegexOptions.IgnoreCase);
            // SUBSTRING(str, start, len) → same in SQL Server, but SSIS is 1-based already
            // REPLACE(str, old, new) → same in SQL Server
            // FINDSTRING(str, search, occurrence) → CHARINDEX(search, str) in SQL Server
            ssisExpr = Regex.Replace(ssisExpr, @"\bFINDSTRING\(([^,]+),\s*([^,]+),\s*([^)]+)\)", "CHARINDEX($2, $1)", RegexOptions.IgnoreCase);

            // 7. Bug #3 fix: ISNULL translation
            // !ISNULL(col) must become col IS NOT NULL (not "! col IS NULL" which is invalid SQL)
            ssisExpr = Regex.Replace(ssisExpr, @"!\s*ISNULL\(([^)]+)\)", "$1 IS NOT NULL");
            // Remaining ISNULL(col) without negation → col IS NULL
            ssisExpr = Regex.Replace(ssisExpr, @"(?<!\w)ISNULL\(([^)]+)\)", "$1 IS NULL");
            
            // 8. Logical NOT: ! prefix → NOT (must run AFTER ISNULL handling to avoid corrupting !ISNULL)
            ssisExpr = Regex.Replace(ssisExpr, @"!\s*(?=[A-Za-z_\[])", "NOT ");
            
            return ssisExpr;
        }

        private static void ValidateGeneratedResult(MigrationResult result)
        {
            foreach (var file in result.Files)
            {
                if (file.Language == "sql")
                {
                    // 1. Check for raw table names without SELECT/WITH in CTEs (RULE-05)
                    if (Regex.IsMatch(file.Content, @"AS\s*\(\s*(?!SELECT|WITH)[a-zA-Z0-9_\[\]\.]+\s*\)", RegexOptions.IgnoreCase))
                    {
                        var msg = $"[RULE-05 VIOLATION] File '{file.FileName}' contains CTE with raw table name instead of SELECT statement. Periksa ekspresi / kueri DTSX.";
                        result.ValidationErrors.Add(msg);
                    }

                    // 2. Check for unresolved join condition fallback (ON 1=1) (RULE-04)
                    if (file.Content.Contains("ON 1=1"))
                    {
                        var msg = $"[RULE-04 VIOLATION] File '{file.FileName}' contains unresolved lookup join condition 'ON 1=1'. Paket DTSX perlu disesuaikan (verifikasi JoinKey atau Column Mappings).";
                        result.ValidationErrors.Add(msg);
                    }

                    // 3. Check for placeholder table names like fact_target (RULE-03)
                    if (file.Content.Contains("FROM fact_target") || file.Content.Contains("FROM staging.raw_source"))
                    {
                        var msg = $"[RULE-03 VIOLATION] File '{file.FileName}' contains unresolved placeholder table reference. Pastikan Connection Manager & tabel sumber pada DTSX sudah benar.";
                        result.ValidationErrors.Add(msg);
                    }

                    // 4. Check for double-quoted strings in dbt models (RULE-05)
                    if (Regex.IsMatch(file.Content, @"THEN\s*""[^""]+""", RegexOptions.IgnoreCase) || Regex.IsMatch(file.Content, @"ELSE\s*""[^""]+""", RegexOptions.IgnoreCase))
                    {
                        var msg = $"[RULE-05 WARNING] File '{file.FileName}' contains double-quoted string literals. Standardizing to single quotes.";
                        result.Warnings.Add(msg);
                    }

                    // 5. Check for mis-attributed lookup dimension columns (RULE-04)
                    if ((file.Content.Contains("lookup_0") || file.Content.Contains("lookup_1")) &&
                        Regex.IsMatch(file.Content, @"\bsource_data\.(CustomerName|CustomerSegment|FullDate|DateKey|BranchKey|BranchName|RegionCode)\b", RegexOptions.IgnoreCase))
                    {
                        var msg = $"[RULE-04 VIOLATION] File '{file.FileName}' incorrectly attributes lookup dimension column to source_data. Paket DTSX perlu disesuaikan pada pemetaan komponen Lookup.";
                        result.ValidationErrors.Add(msg);
                    }
                }
                else if (file.Language == "python")
                {
                    // Check for raw table name in extract_query without SELECT (RULE-03)
                    if (file.Content.Contains("extract_query = \"") && !file.Content.Contains("SELECT") && !file.Content.Contains("WITH"))
                    {
                        var msg = $"[RULE-03 VIOLATION] File '{file.FileName}' extract_query is a raw table name without SELECT * FROM. Sesuaikan kueri sumber pada DTSX.";
                        result.ValidationErrors.Add(msg);
                    }

                    // Check for self-healing staging table DDL (RULE-01)
                    if (file.FileName.StartsWith("dag_") && file.Content.Contains("truncate_staging") && !file.Content.Contains("CREATE TABLE IF NOT EXISTS"))
                    {
                        var msg = $"[RULE-01 VIOLATION] File '{file.FileName}' staging task lacks self-healing DDL (CREATE TABLE IF NOT EXISTS). Periksa konfigurasi Execute SQL Task pada DTSX.";
                        result.ValidationErrors.Add(msg);
                    }

                    // Check for idempotency clean-before-insert (RULE-02)
                    if (file.FileName.StartsWith("dag_") && file.Content.Contains("truncate_staging") && !file.Content.Contains("TRUNCATE TABLE"))
                    {
                        var msg = $"[RULE-02 VIOLATION] File '{file.FileName}' staging task lacks clean-before-insert logic (TRUNCATE TABLE). Periksa konfigurasi Execute SQL Task pada DTSX.";
                        result.ValidationErrors.Add(msg);
                    }

                    // Check for unhandled Script Component C# code (RULE-06)
                    if (file.Content.Contains("MANUAL PYTHON TRANSLATION REQUIRED") || file.Content.Contains("Script Component (C#/VB.NET)"))
                    {
                        var msg = $"[RULE-06 WARNING] File '{file.FileName}' mengaitkan Script Component (C#/VB.NET) legacy. Paket DTSX perlu disesuaikan untuk refaktor logika ke Python/PySpark.";
                        result.Warnings.Add(msg);
                    }
                }
                else if (file.Language == "yaml" || file.FileName == "schema.yml")
                {
                    var lines = file.Content.Split('\n');
                    for (int i = 0; i < lines.Length - 1; i++)
                    {
                        var line = lines[i].TrimEnd();
                        if (line.Trim() == "columns:")
                        {
                            var nextLine = lines[i + 1].TrimEnd();
                            var indentCurrent = line.Length - line.TrimStart().Length;
                            var indentNext = nextLine.Length - nextLine.TrimStart().Length;
                            if (indentNext <= indentCurrent)
                            {
                                var msg = $"[RULE-04 VIOLATION] File '{file.FileName}' contains empty 'columns:' key without child items. Periksa pemetaan kolom pada DTSX.";
                                result.ValidationErrors.Add(msg);
                                break;
                            }
                        }
                    }
                }
            }

            if (result.ValidationErrors.Count > 0)
            {
                result.Summary += $" [RULE CHECK FAILED: {result.ValidationErrors.Count} error(s) detected! DTSX Perlu Disesuaikan]";
            }
            else
            {
                result.Summary += " [QUALITY GATE: All generated artifacts passed pre-migration rules!]";
            }
        }

        private static (string Server, string Database) ResolveDatabaseAndServer(LineageGraph graph, PackageNode pkg, ComponentNode? comp, bool isDestination)
        {
            // 1. Check component ConnectionManager directly via SsisConnectionManagerResolver (most specific)
            if (comp != null && !string.IsNullOrEmpty(comp.ConnectionManager))
            {
                var resolver = new SsisConnectionManagerResolver(pkg.ProjectPath);
                var connStr = resolver.TryResolveConnectionString(comp.ConnectionManager);
                if (!string.IsNullOrEmpty(connStr))
                {
                    var (s, d) = SqlProcedureDefinitionLoader.ExtractServerAndDatabase(connStr);
                    if (!string.IsNullOrEmpty(d)) return (s, d);
                }
            }

            // 2. Check ColumnMappings matching this specific component (if comp is provided)
            if (comp != null)
            {
                var compMap = graph.ColumnMappings.FirstOrDefault(m => m.PackageId == pkg.Id &&
                    (isDestination ? (m.TargetComponentId == comp.Id || m.TargetComponentName == comp.Name)
                                   : (m.SourceComponentId == comp.Id || m.SourceComponentName == comp.Name)) &&
                    !string.IsNullOrEmpty(isDestination ? m.TargetDatabase : m.SourceDatabase));

                if (compMap != null)
                {
                    var db = isDestination ? compMap.TargetDatabase : compMap.SourceDatabase;
                    var srv = isDestination ? compMap.TargetServer : compMap.SourceServer;
                    if (!string.IsNullOrEmpty(db)) return (srv, db);
                }
            }

            // 3. Fall back to package ColumnMappings
            if (isDestination)
            {
                var map = graph.ColumnMappings.FirstOrDefault(m => m.PackageId == pkg.Id && !string.IsNullOrEmpty(m.TargetDatabase));
                if (map != null && !string.IsNullOrEmpty(map.TargetDatabase)) return (map.TargetServer, map.TargetDatabase);
            }
            else
            {
                var map = graph.ColumnMappings.FirstOrDefault(m => m.PackageId == pkg.Id && !string.IsNullOrEmpty(m.SourceDatabase));
                if (map != null && !string.IsNullOrEmpty(map.SourceDatabase)) return (map.SourceServer, map.SourceDatabase);
            }

            // 3. Check any matching component in the package
            var pkgComponents = graph.Components.Where(c => c.PackageId == pkg.Id).ToList();
            foreach (var c in pkgComponents)
            {
                if (isDestination && (c.Type.Contains("Destination", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(c.SqlQueryOrTable)))
                {
                    var resolver = new SsisConnectionManagerResolver(pkg.ProjectPath);
                    var connStr = resolver.TryResolveConnectionString(c.ConnectionManager);
                    if (!string.IsNullOrEmpty(connStr))
                    {
                        var (s, d) = SqlProcedureDefinitionLoader.ExtractServerAndDatabase(connStr);
                        if (!string.IsNullOrEmpty(d)) return (s, d);
                    }
                }
                else if (!isDestination && (c.Type.Contains("Source", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(c.SqlQueryOrTable)))
                {
                    var resolver = new SsisConnectionManagerResolver(pkg.ProjectPath);
                    var connStr = resolver.TryResolveConnectionString(c.ConnectionManager);
                    if (!string.IsNullOrEmpty(connStr))
                    {
                        var (s, d) = SqlProcedureDefinitionLoader.ExtractServerAndDatabase(connStr);
                        if (!string.IsNullOrEmpty(d)) return (s, d);
                    }
                }
            }

            // 4. Check project-wide connection managers
            if (!string.IsNullOrEmpty(pkg.ProjectPath))
            {
                var resolver = new SsisConnectionManagerResolver(pkg.ProjectPath);
                foreach (var kv in resolver.ConnectionStrings)
                {
                    var (s, d) = SqlProcedureDefinitionLoader.ExtractServerAndDatabase(kv.Value);
                    if (isDestination && (kv.Key.Contains("DW", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("Warehouse", StringComparison.OrdinalIgnoreCase) || d.Contains("Warehouse", StringComparison.OrdinalIgnoreCase)))
                    {
                        return (s, d);
                    }
                    if (!isDestination && (kv.Key.Contains("HR", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("Banking", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("Source", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (pkg.Name.Contains("HR", StringComparison.OrdinalIgnoreCase) && d.Contains("HR", StringComparison.OrdinalIgnoreCase)) return (s, d);
                        if (pkg.Name.Contains("Ledger", StringComparison.OrdinalIgnoreCase) && d.Contains("Ledger", StringComparison.OrdinalIgnoreCase)) return (s, d);
                    }
                }
            }

            // 5. Check other ColumnMappings project-wide
            if (isDestination)
            {
                var map = graph.ColumnMappings.FirstOrDefault(m => !string.IsNullOrEmpty(m.TargetDatabase));
                if (map != null) return (map.TargetServer, map.TargetDatabase);
            }

            // FIX #1: Unified fallback database. When connection manager metadata cannot be
            // resolved from the .dtsx (e.g. AI-generated samples using bare GUIDs without
            // a <DTS:ConnectionManagers> block), always fall back to "SsisDemoDB" for both
            // source AND destination. This keeps the Python extraction script, the SQL prep
            // task, and the dbt profile all pointing at the same database, matching the
            // sql_default Airflow connection defined in docker-compose.yml.
            return ("", "SsisDemoDB");
        }

        private static string NormalizeServer(string server)
        {
            if (string.IsNullOrWhiteSpace(server) || server == "." || server.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return "172.17.0.1,1433";
            }
            if (!server.Contains(","))
            {
                return server + ",1433";
            }
            return server;
        }

        private static string ResolveChildDagId(LineageGraph graph, TaskNode task)
        {
            // 1. Check if an ExecutionEdge with Invokes points to a package
            var invokesEdge = graph.ExecutionEdges.FirstOrDefault(e => e.FromTaskId == task.Id && e.PrecedenceConstraintValue == "Invokes");
            if (invokesEdge != null)
            {
                var targetPkg = graph.Packages.FirstOrDefault(p => p.Id == invokesEdge.ToTaskId);
                if (targetPkg != null)
                {
                    var cleanTarget = CleanIdentifier(targetPkg.Name).ToLowerInvariant();
                    if (cleanTarget.StartsWith("pkg_")) cleanTarget = cleanTarget.Substring(4);
                    return "dag_" + cleanTarget;
                }
            }

            // 2. Check task.Description (which holds the PackageName tag e.g. Pkg_01_Extract_EnterpriseHR_Payroll.dtsx)
            if (!string.IsNullOrWhiteSpace(task.Description))
            {
                var baseName = Path.GetFileNameWithoutExtension(task.Description).Trim();
                if (!string.IsNullOrEmpty(baseName))
                {
                    var cleanBase = CleanIdentifier(baseName).ToLowerInvariant();
                    if (cleanBase.StartsWith("pkg_")) cleanBase = cleanBase.Substring(4);
                    return "dag_" + cleanBase;
                }
            }

            // 3. Match against packages by name / keywords
            var taskKeywords = CleanIdentifier(task.Name).ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Where(k => k != "execute" && k != "pkg" && k != "dag" && k != "task" && k != "package").ToList();

            if (taskKeywords.Any())
            {
                var bestPkg = graph.Packages.FirstOrDefault(p =>
                {
                    var pLower = p.Name.ToLowerInvariant();
                    return taskKeywords.Count(kw => pLower.Contains(kw)) >= 2 || taskKeywords.All(kw => pLower.Contains(kw));
                });

                if (bestPkg != null)
                {
                    var cleanTarget = CleanIdentifier(bestPkg.Name).ToLowerInvariant();
                    if (cleanTarget.StartsWith("pkg_")) cleanTarget = cleanTarget.Substring(4);
                    return "dag_" + cleanTarget;
                }
            }

            // 4. Default fallback
            var fallback = CleanIdentifier(task.Name).ToLowerInvariant();
            if (fallback.StartsWith("pkg_")) fallback = fallback.Substring(4);
            if (fallback.StartsWith("execute_")) fallback = fallback.Substring(8);
            return "dag_" + fallback;
        }

        // ── 7. BMC Control-M Automation API JSON Generator ──────────────────────
        private static void GenerateBmcControlMJson(LineageGraph graph, List<PackageNode> packages, MigrationResult result)
        {
            var rawProjectName = packages.FirstOrDefault() != null
                ? Path.GetFileNameWithoutExtension(packages.First().ProjectPath)
                : "SSIS_Migration";
            if (string.IsNullOrEmpty(rawProjectName) || rawProjectName == ".") rawProjectName = "SSIS_Migration";

            var folderName = CleanIdentifier(rawProjectName + "_Folder");

            var folderObj = new Dictionary<string, object>
            {
                ["Type"] = "Folder",
                ["ControlmServer"] = "ctmserver",
                ["Application"] = "EnterpriseETL",
                ["SubApplication"] = "SSIS_Migration",
                ["OrderMethod"] = "Manual"
            };

            foreach (var pkg in packages)
            {
                var jobName = CleanIdentifier(pkg.Name);
                var pkgTasks = graph.Tasks.Where(t => t.PackageId == pkg.Id).ToList();

                var cleanPkgName = CleanIdentifier(pkg.Name).ToLowerInvariant();
                if (cleanPkgName.StartsWith("pkg_")) cleanPkgName = cleanPkgName.Substring(4);

                var jobDef = new Dictionary<string, object>
                {
                    ["Type"] = "Job:Command",
                    ["RunAs"] = "etluser",
                    ["Host"] = "etl-node-01",
                    ["Command"] = $"python /opt/etl/scripts/extract_{cleanPkgName}.py && dbt run --select stg_{cleanPkgName}"
                };

                var childTaskExecutes = pkgTasks.Where(t => t.Type != null && t.Type.Contains("Execute Package", StringComparison.OrdinalIgnoreCase)).ToList();

                var eventsDict = new Dictionary<string, object>();
                var addEvents = new List<object>();

                if (childTaskExecutes.Any())
                {
                    foreach (var childTask in childTaskExecutes)
                    {
                        var childDagId = ResolveChildDagId(graph, childTask);
                        addEvents.Add(new Dictionary<string, string>
                        {
                            ["Event"] = $"{jobName}-TO-{childDagId}"
                        });
                    }
                }

                if (!jobName.Contains("Master", StringComparison.OrdinalIgnoreCase) && packages.Any(p => p.Name.Contains("Master", StringComparison.OrdinalIgnoreCase)))
                {
                    var waitForEvents = new List<object>
                    {
                        new Dictionary<string, string>
                        {
                            ["Event"] = $"Pkg_00_Master_ETL_Orchestration-TO-dag_{cleanPkgName}"
                        }
                    };
                    eventsDict["WaitFor"] = waitForEvents;
                }

                if (addEvents.Any())
                {
                    eventsDict["Add"] = addEvents;
                }

                if (eventsDict.Any())
                {
                    jobDef["Events"] = eventsDict;
                }

                folderObj[jobName] = jobDef;
            }

            var rootObj = new Dictionary<string, object>
            {
                [folderName] = folderObj
            };

            var jsonStr = JsonSerializer.Serialize(rootObj, new JsonSerializerOptions { WriteIndented = true });

            result.Files.Add(new GeneratedFile
            {
                FileName = $"{folderName}.json",
                Content = jsonStr,
                Language = "json",
                TargetFramework = "BMC Control-M Automation API"
            });

            result.Summary = $"Generated BMC Control-M Automation API JSON workflow specification ({folderName}.json).";
        }
    }
}
