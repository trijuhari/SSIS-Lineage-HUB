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
        ConsolidatedSql
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
            }

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
                var pkgMappings = graph.ColumnMappings.Where(m => m.PackageId == pkg.Id).ToList();

                var modelName = CleanIdentifier(pkg.Name).ToLowerInvariant();
                if (modelName.StartsWith("pkg_")) modelName = modelName.Substring(4);
                modelName = "stg_" + modelName;

                schemaYaml.AppendLine($"  - name: {modelName}");
                schemaYaml.AppendLine($"    description: \"Auto-converted from SSIS Package '{pkg.Name}'\"");
                schemaYaml.AppendLine("    columns:");

                var targetCols = pkgMappings.Select(m => m.TargetColumnName).Distinct().Where(c => !string.IsNullOrEmpty(c));
                foreach (var col in targetCols)
                {
                    schemaYaml.AppendLine($"      - name: {col}");
                    schemaYaml.AppendLine($"        description: \"Mapped from source column\"");
                }

                // Generate Model SQL (.sql)
                var sb = new StringBuilder();
                sb.AppendLine($"-- dbt Model: {modelName}.sql");
                sb.AppendLine($"-- Converted from SSIS Package: {pkg.Name}");
                sb.AppendLine();

                var sources = pkgComponents.Where(c => c.Type.Contains("Source", StringComparison.OrdinalIgnoreCase)).ToList();
                var destinations = pkgComponents.Where(c => c.Type.Contains("Destination", StringComparison.OrdinalIgnoreCase)).ToList();

                sb.AppendLine("WITH source_data AS (");
                if (sources.Any() && !string.IsNullOrEmpty(sources.First().SqlQueryOrTable))
                {
                    sb.AppendLine("    -- Extracted from SSIS OLE DB Source Query");
                    var srcQuery = sources.First().SqlQueryOrTable.Trim();
                    foreach (var line in srcQuery.Split('\n'))
                    {
                        sb.AppendLine("    " + line);
                    }
                }
                else
                {
                    sb.AppendLine("    SELECT * FROM {{ source('raw_staging', 'source_table') }}");
                }
                sb.AppendLine("),");
                sb.AppendLine();
                sb.AppendLine("transformed AS (");
                sb.AppendLine("    SELECT");

                if (pkgMappings.Any())
                {
                    var mapLines = new List<string>();
                    foreach (var m in pkgMappings.DistinctBy(x => x.TargetColumnName))
                    {
                        var expr = !string.IsNullOrEmpty(m.SourceExpression)
                            ? m.SourceExpression
                            : $"source_data.{m.SourceColumnName}";
                        mapLines.Add($"        {expr} AS {m.TargetColumnName}");
                    }
                    sb.AppendLine(string.Join(",\n", mapLines));
                }
                else
                {
                    sb.AppendLine("        source_data.*");
                }

                sb.AppendLine("    FROM source_data");
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
                sb.AppendLine();
                sb.AppendLine("from pyspark.sql import SparkSession");
                sb.AppendLine("from pyspark.sql import functions as F");
                sb.AppendLine();
                sb.AppendLine("spark = SparkSession.builder.appName('" + pkg.Name + "').getOrCreate()");
                sb.AppendLine();
                sb.AppendLine("# JDBC Connection Config");
                sb.AppendLine("jdbc_url = 'jdbc:sqlserver://localhost:1433;databaseName=SsisDemoDB'");
                sb.AppendLine("connection_properties = {");
                sb.AppendLine("    'user': 'sa',");
                sb.AppendLine("    'password': 'db_password_here',");
                sb.AppendLine("    'driver': 'com.microsoft.sqlserver.jdbc.SQLServerDriver'");
                sb.AppendLine("}");
                sb.AppendLine();

                var sourceComp = pkgComponents.FirstOrDefault(c => c.Type.Contains("Source", StringComparison.OrdinalIgnoreCase));
                var destComp = pkgComponents.FirstOrDefault(c => c.Type.Contains("Destination", StringComparison.OrdinalIgnoreCase));

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
                            selectExprs.Add($"    F.expr(\"{m.SourceExpression.Replace("\"", "'")}\").alias(\"{m.TargetColumnName}\")");
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
                            ? m.SourceExpression
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

        private static string CleanIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";
            var clean = Regex.Replace(name, @"[^\w]", "_");
            return Regex.Replace(clean, @"_+", "_").Trim('_');
        }
    }
}
