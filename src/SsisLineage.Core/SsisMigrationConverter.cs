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
        PythonPandas
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
                case MigrationTarget.AirflowDag:
                    GenerateAirflowDags(graph, packagesToConvert, result);
                    break;
                case MigrationTarget.PythonPandas:
                    GeneratePythonPandasScripts(graph, packagesToConvert, result);
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
                var pkgMappings = graph.ColumnMappings.Where(m => m.PackageId == pkg.Id && (m.OperationType == null || !m.OperationType.StartsWith("SQL_PROC_"))).ToList();

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
                // Use same naming convention as Python generator: strip brackets, replace dots with underscores
                var rawLandingTable = destinations.FirstOrDefault() != null && !string.IsNullOrEmpty(destinations.First().SqlQueryOrTable)
                    ? destinations.First().SqlQueryOrTable
                    : "fact_target";
                var landingTable = Regex.Replace(rawLandingTable, @"[\[\]]", "").Replace(".", "_").Trim();
                if (string.IsNullOrEmpty(landingTable)) landingTable = "fact_target";

                sb.AppendLine("WITH source_data AS (");
                sb.AppendLine($"    -- Extracted from Landing Zone (Populated by Python)");
                sb.AppendLine($"    SELECT * FROM dbo.{landingTable}");
                sb.AppendLine(")");
                
                var lookups = pkgComponents.Where(c => c.Type != null && c.Type.Contains("Lookup", StringComparison.OrdinalIgnoreCase)).ToList();
                for (int i = 0; i < lookups.Count; i++)
                {
                    var lkp = lookups[i];
                    if (!string.IsNullOrEmpty(lkp.SqlQueryOrTable))
                    {
                        sb.AppendLine($",\nlookup_{i} AS (");
                        sb.AppendLine($"    {lkp.SqlQueryOrTable}");
                        sb.AppendLine(")");
                    }
                }
                
                sb.AppendLine();
                sb.AppendLine(",\ntransformed AS (");
                sb.AppendLine("    SELECT");

                if (pkgMappings.Any())
                {
                    var mapLines = new List<string>();
                    foreach (var m in pkgMappings.DistinctBy(x => x.TargetColumnName))
                    {
                        var expr = !string.IsNullOrEmpty(m.SourceExpression)
                            ? TranslateSsisExpressionToSql(m.SourceExpression)
                            : $"source_data.{m.SourceColumnName}";
                        
                        // Heuristic casting for simple column mappings
                        if (string.IsNullOrEmpty(m.SourceExpression))
                        {
                            var targetNameLower = m.TargetColumnName.ToLowerInvariant();
                            if (targetNameLower.Contains("quantity") || targetNameLower.Contains("count"))
                            {
                                expr = $"CAST({expr} AS INT)";
                            }
                            else if (targetNameLower.Contains("amount") || targetNameLower.Contains("price") || targetNameLower.Contains("total"))
                            {
                                expr = $"CAST({expr} AS DECIMAL(18,2))";
                            }
                            else if (targetNameLower.Contains("date"))
                            {
                                expr = $"CAST({expr} AS DATETIME)";
                            }
                        }
                        
                        // Default regex to ensure proper qualification, though heuristic logic shouldn't mess this up
                        expr = Regex.Replace(expr, @"\b[a-zA-Z_]\w*\.([a-zA-Z_]\w*)", "source_data.$1");
                        mapLines.Add($"        {expr} AS {m.TargetColumnName}");
                    }
                    sb.AppendLine(string.Join(",\n", mapLines));
                }
                else
                {
                    sb.AppendLine("        source_data.*");
                }

                sb.AppendLine("    FROM source_data");
                for (int i = 0; i < lookups.Count; i++)
                {
                     sb.AppendLine($"    LEFT JOIN lookup_{i} ON 1=1 /* TODO: Replace with actual Join Condition */");
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
                sb.AppendLine();
                sb.AppendLine("def extract_and_load():");
                sb.AppendLine($"    print(f\"[{{datetime.now()}}] Starting extraction for {pkg.Name}...\")");
                sb.AppendLine();
                sb.AppendLine("    # TODO: Use environment variables or secret manager for credentials");
                sb.AppendLine("    conn_str = (");
                sb.AppendLine("        r'DRIVER={ODBC Driver 18 for SQL Server};'");
                sb.AppendLine("        r'SERVER=172.17.0.1,1433;'");
                sb.AppendLine("        r'DATABASE=SsisDemoDB;'");
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

                var sourceComp = pkgComponents.FirstOrDefault(c => c.Type.Contains("Source", StringComparison.OrdinalIgnoreCase));
                var destComp = pkgComponents.FirstOrDefault(c => c.Type.Contains("Destination", StringComparison.OrdinalIgnoreCase));

                sb.AppendLine("    # Define source extraction query");
                if (sourceComp != null && !string.IsNullOrEmpty(sourceComp.SqlQueryOrTable))
                {
                    var rawSql = sourceComp.SqlQueryOrTable.Trim();
                    // Normalize staging table references (e.g. stg.RawCustomers or [stg].[RawCustomers] -> dbo.stg_RawCustomers)
                    rawSql = Regex.Replace(rawSql, @"\[?stg\]?\.\[?(\w+)\]?", "dbo.stg_$1", RegexOptions.IgnoreCase);
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
                sb.AppendLine("            r'SERVER=172.17.0.1,1433;'");
                sb.AppendLine("            r'DATABASE=SsisDemoDB;'");
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
                sb.AppendLine("            if 'int' in dt_str: return 'INT'");
                sb.AppendLine("            if 'float' in dt_str: return 'DECIMAL(18,2)'");
                sb.AppendLine("            if 'datetime' in dt_str: return 'DATETIME'");
                sb.AppendLine("            return 'NVARCHAR(MAX)'");
                sb.AppendLine("            ");
                sb.AppendLine("        cols_ddl = ', '.join([f'[{c}] {map_dtype(df[c].dtype)}' for c in df.columns])");
                sb.AppendLine("        cursor.execute(f'CREATE TABLE dbo.{target_table} ({cols_ddl})')");
                sb.AppendLine();
                sb.AppendLine("        # Bulk insert");
                sb.AppendLine("        placeholders = ', '.join(['?' for _ in df.columns])");
                sb.AppendLine("        rows = [tuple(str(v) if v is not None else None for v in row) for row in df.itertuples(index=False)]");
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
                sb.AppendLine();
                sb.AppendLine("default_args = {");
                sb.AppendLine("    'owner': 'data_engineering',");
                sb.AppendLine("    'depends_on_past': False,");
                sb.AppendLine("    'email_on_failure': False,");
                sb.AppendLine("    'email_on_retry': False,");
                sb.AppendLine("    'retries': 1,");
                sb.AppendLine("    'retry_delay': timedelta(minutes=5),");
                sb.AppendLine("}");
                sb.AppendLine();
                sb.AppendLine($"with DAG(");
                sb.AppendLine($"    dag_id='{dagName}',");
                sb.AppendLine($"    default_args=default_args,");
                sb.AppendLine($"    description='Auto-converted from SSIS Package {pkg.Name}',");
                sb.AppendLine($"    schedule=None,");
                sb.AppendLine($"    start_date=datetime(2026, 1, 1),");
                sb.AppendLine($"    catchup=False,");
                sb.AppendLine($"    tags=['ssis_migration'],");
                sb.AppendLine($") as dag:");
                sb.AppendLine();
                sb.AppendLine("    start_pipeline = EmptyOperator(task_id='start_pipeline')");
                sb.AppendLine("    end_pipeline = EmptyOperator(task_id='end_pipeline')");
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
                        var sqlQuery = sqlComp != null && !string.IsNullOrEmpty(sqlComp.SqlQueryOrTable) 
                                       ? sqlComp.SqlQueryOrTable.Replace("'", "\\'").Replace("\n", " ").Replace("\r", "") 
                                       : "-- TODO: Insert SQL from SSIS task";
                                       
                        sb.AppendLine($"    {taskId} = SQLExecuteQueryOperator(");
                        sb.AppendLine($"        task_id='{taskId}',");
                        sb.AppendLine($"        conn_id='sql_default',");
                        sb.AppendLine($"        sql='{sqlQuery}',");
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
                        sb.AppendLine($"        bash_command='cd /opt/airflow/dags/dbt_project && dbt run --profiles-dir . --select {dbtModelName}',");
                        sb.AppendLine($"    )");
                        sb.AppendLine();
                        sb.AppendLine($"    {taskId}_extract >> {taskId}_dbt");
                    }
                    else if (tType.Contains("executepackagetask"))
                    {
                        var childPkgName = task.Name.Replace(" ", "_").ToLowerInvariant();
                        sb.AppendLine($"    {taskId} = TriggerDagRunOperator(");
                        sb.AppendLine($"        task_id='{taskId}',");
                        sb.AppendLine($"        trigger_dag_id='dag_{childPkgName}',");
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
                sb.AppendLine("    # Set up task dependencies");
                if (taskNames.Count > 0)
                {
                    var firstTask = tasks.First();
                    var fName = CleanIdentifier(firstTask.Name).ToLowerInvariant();
                    var fType = firstTask.Type?.ToLowerInvariant() ?? "";
                    var fRef = (fType.Contains("data flow") || fType.Contains("pipeline")) ? $"{fName}_extract" : fName;
                    sb.AppendLine($"    start_pipeline >> {fRef}");
                    
                    var execEdges = graph.ExecutionEdges
                        .Where(e => tasks.Any(t => t.Id == e.FromTaskId) && tasks.Any(t => t.Id == e.ToTaskId))
                        .ToList();

                    if (execEdges.Count > 0)
                    {
                        foreach (var edge in execEdges)
                        {
                            var fromTask = tasks.FirstOrDefault(t => t.Id == edge.FromTaskId);
                            var toTask = tasks.FirstOrDefault(t => t.Id == edge.ToTaskId);
                            if (fromTask != null && toTask != null)
                            {
                                var fromName = CleanIdentifier(fromTask.Name).ToLowerInvariant();
                                var toName = CleanIdentifier(toTask.Name).ToLowerInvariant();
                                
                                var fromType = fromTask.Type?.ToLowerInvariant() ?? "";
                                var toType = toTask.Type?.ToLowerInvariant() ?? "";
                                
                                var fromRef = (fromType.Contains("data flow") || fromType.Contains("pipeline")) ? $"{fromName}_dbt" : fromName;
                                var toRef = (toType.Contains("data flow") || toType.Contains("pipeline")) ? $"{toName}_extract" : toName;
                                
                                sb.AppendLine($"    {fromRef} >> {toRef}");
                            }
                        }
                        
                        var lastTasks = tasks.Where(t => !execEdges.Any(e => e.FromTaskId == t.Id)).ToList();
                        foreach (var lTask in lastTasks)
                        {
                            var lName = CleanIdentifier(lTask.Name).ToLowerInvariant();
                            var lType = lTask.Type?.ToLowerInvariant() ?? "";
                            var lRef = (lType.Contains("data flow") || lType.Contains("pipeline")) ? $"{lName}_dbt" : lName;
                            
                            sb.AppendLine($"    {lRef} >> end_pipeline");
                        }
                    }
                    else
                    {
                        for (int i = 0; i < tasks.Count - 1; i++)
                        {
                            var fromTask = tasks[i];
                            var toTask = tasks[i + 1];
                            
                            var fromName = CleanIdentifier(fromTask.Name).ToLowerInvariant();
                            var toName = CleanIdentifier(toTask.Name).ToLowerInvariant();
                            
                            var fromType = fromTask.Type?.ToLowerInvariant() ?? "";
                            var toType = toTask.Type?.ToLowerInvariant() ?? "";
                            
                            var fromRef = (fromType.Contains("data flow") || fromType.Contains("pipeline")) ? $"{fromName}_dbt" : fromName;
                            var toRef = (toType.Contains("data flow") || toType.Contains("pipeline")) ? $"{toName}_extract" : toName;
                            
                            sb.AppendLine($"    {fromRef} >> {toRef}");
                        }
                        
                        var lastTask = tasks.Last();
                        var lName = CleanIdentifier(lastTask.Name).ToLowerInvariant();
                        var lType = lastTask.Type?.ToLowerInvariant() ?? "";
                        var lRef = (lType.Contains("data flow") || lType.Contains("pipeline")) ? $"{lName}_dbt" : lName;
                        sb.AppendLine($"    {lRef} >> end_pipeline");
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
        /// Translates SSIS expressions to ANSI SQL syntax
        /// </summary>
        private static string TranslateSsisExpressionToSql(string ssisExpr)
        {
            if (string.IsNullOrEmpty(ssisExpr)) return ssisExpr;
            
            // 1. Ternary Operator: Condition ? TrueVal : FalseVal -> CASE WHEN Condition THEN TrueVal ELSE FalseVal END
            // Process from right to left to handle nested ternaries gracefully
            var ternaryRegex = new Regex(@"([^?:]+)\?([^?:]+):([^?:]+)", RegexOptions.RightToLeft);
            while (ternaryRegex.IsMatch(ssisExpr))
            {
                ssisExpr = ternaryRegex.Replace(ssisExpr, match => 
                {
                    var cond = match.Groups[1].Value.Trim();
                    var tVal = match.Groups[2].Value.Trim();
                    var fVal = match.Groups[3].Value.Trim();
                    
                    bool strippedLeft = false, strippedRight = false;
                    if (cond.StartsWith("(") && !cond.EndsWith(")")) { cond = cond.Substring(1).Trim(); strippedLeft = true; }
                    if (fVal.EndsWith(")") && !fVal.StartsWith("(")) { fVal = fVal.Substring(0, fVal.Length - 1).Trim(); strippedRight = true; }
                    
                    var res = $"CASE WHEN {cond} THEN {tVal} ELSE {fVal} END";
                    if (strippedLeft) res = "(" + res;
                    if (strippedRight) res = res + ")";
                    return res;
                }, 1);
            }
            
            // 2. Typecasts (DT_WSTR, 50) -> CAST(... AS VARCHAR(50))
            ssisExpr = Regex.Replace(ssisExpr, @"\(DT_WSTR,\s*\d+\)", ""); // Just strip them for now to avoid complexity
            ssisExpr = Regex.Replace(ssisExpr, @"\(DT_I4\)", "");
            
            // 3. Equality operators == to =
            ssisExpr = ssisExpr.Replace("==", "=");
            
            // 4. SSIS Variables @[User::VarName] or @[$Package::VarName] -> {{ var('VarName') }}
            ssisExpr = Regex.Replace(ssisExpr, @"@\[(?:User|\$Package)::([^\]]+)\]", "{{ var('$1') }}");
            
            // 5. ISNULL -> IS NULL for conditions
            ssisExpr = Regex.Replace(ssisExpr, @"ISNULL\(([^)]+)\)", "$1 IS NULL");
            
            return ssisExpr;
        }
    }
}
