using System.Reflection;
using SsisLineage.Core;
using SsisLineage.Core.Models;

namespace SsisLineage.Tests;

public class SsisMigrationEndToEndTests
{
    [Fact]
    public void Pkg02_TransformOrderSummary_GeneratesValidDbtSql()
    {
        // Parse the actual sample SSIS project
        var projectDir = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
            "..", "..", "..", "..", "..", "sample-ssis-project-4");

        // Resolve to absolute path
        projectDir = Path.GetFullPath(projectDir);

        // Skip if sample project doesn't exist (CI environments)
        if (!Directory.Exists(projectDir))
            return; // Skip silently

        var parser = new SsisPackageParser(projectDir);
        var pkgPath = Path.Combine(projectDir, "Pkg_02_Transform_OrderSummary.dtsx");
        Assert.True(File.Exists(pkgPath), $"Package not found: {pkgPath}");

        var graph = parser.Parse(pkgPath);
        Assert.NotEmpty(graph.Packages);
        Assert.NotEmpty(graph.ColumnMappings);

        // Verify the VipBonusTier mapping was parsed
        var vipMapping = graph.ColumnMappings.FirstOrDefault(m => m.TargetColumnName == "VipBonusTier");
        Assert.NotNull(vipMapping);
        Assert.NotEmpty(vipMapping.SourceExpression!);
        Assert.Contains("TIER_GOLD", vipMapping.SourceExpression);

        // Generate dbt SQL
        var result = SsisMigrationConverter.ConvertProject(graph, MigrationTarget.DbtSql);
        Assert.True(result.Files.Count >= 2); // schema.yml + model

        var modelFile = result.Files.FirstOrDefault(f => f.FileName.Contains("ordersummary"));
        Assert.NotNull(modelFile);

        // Verify single-quoted strings (not double-quoted)
        Assert.Contains("'TIER_GOLD'", modelFile.Content);
        Assert.Contains("'TIER_STANDARD'", modelFile.Content);
        Assert.DoesNotContain("\"TIER_GOLD\"", modelFile.Content);
        Assert.DoesNotContain("\"TIER_STANDARD\"", modelFile.Content);

        // Verify CASE WHEN ... END structure
        Assert.Contains("CASE WHEN", modelFile.Content);
        Assert.Contains("END", modelFile.Content);

        // Verify column alias is present
        Assert.Contains("AS VipBonusTier", modelFile.Content);

        // Print the generated SQL for human inspection
        Console.WriteLine("=== Generated dbt SQL for Pkg_02_Transform_OrderSummary ===");
        Console.WriteLine(modelFile.Content);
        Console.WriteLine("=== END ===");
    }

    [Fact]
    public void Pkg01_ExtractCustomerOrders_GeneratesValidDbtSql()
    {
        var projectDir = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
            "..", "..", "..", "..", "..", "sample-ssis-project-4");
        projectDir = Path.GetFullPath(projectDir);

        if (!Directory.Exists(projectDir))
            return;

        var parser = new SsisPackageParser(projectDir);
        var pkgPath = Path.Combine(projectDir, "Pkg_01_Extract_CustomerOrders.dtsx");
        Assert.True(File.Exists(pkgPath), $"Package not found: {pkgPath}");

        var graph = parser.Parse(pkgPath);
        Assert.NotEmpty(graph.Packages);

        // Generate dbt SQL
        var result = SsisMigrationConverter.ConvertProject(graph, MigrationTarget.DbtSql);

        var modelFile = result.Files.FirstOrDefault(f => f.FileName.Contains("customerorders"));
        Assert.NotNull(modelFile);

        // Verify lookup CTE exists
        Assert.Contains("lookup_0", modelFile.Content);
        Assert.Contains("Customers", modelFile.Content);

        // Verify derived column expressions
        Assert.Contains("AS FinalPrice", modelFile.Content);
        Assert.Contains("AS IsHighValue", modelFile.Content);
        Assert.Contains("CASE WHEN", modelFile.Content);

        // Verify lookup join
        Assert.Contains("LEFT JOIN lookup_0", modelFile.Content);

        Console.WriteLine("=== Generated dbt SQL for Pkg_01_Extract_CustomerOrders ===");
        Console.WriteLine(modelFile.Content);
        Console.WriteLine("=== END ===");
    }

    [Fact]
    public void Pkg01_ExtractCustomerOrders_GeneratesValidAirflowDag()
    {
        var projectDir = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
            "..", "..", "..", "..", "..", "sample-ssis-project-4");
        projectDir = Path.GetFullPath(projectDir);

        if (!Directory.Exists(projectDir))
            return;

        var parser = new SsisPackageParser(projectDir);
        var pkgPath = Path.Combine(projectDir, "Pkg_01_Extract_CustomerOrders.dtsx");
        var graph = parser.Parse(pkgPath);

        // Generate Airflow DAG
        var result = SsisMigrationConverter.ConvertProject(graph, MigrationTarget.AirflowDag);
        Assert.NotEmpty(result.Files);

        var dagFile = result.Files.First();
        Assert.Contains("from airflow import DAG", dagFile.Content);
        Assert.Contains("start_pipeline", dagFile.Content);
        Assert.Contains("end_pipeline", dagFile.Content);

        // Verify truncate task comes before extract
        Assert.Contains("SQLExecuteQueryOperator", dagFile.Content);
        Assert.Contains("BashOperator", dagFile.Content);

        Console.WriteLine("=== Generated Airflow DAG for Pkg_01_Extract_CustomerOrders ===");
        Console.WriteLine(dagFile.Content);
        Console.WriteLine("=== END ===");
    }

    [Fact]
    public void Pkg01_ExtractCustomerOrders_GeneratesValidPythonExtractScript()
    {
        var projectDir = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
            "..", "..", "..", "..", "..", "sample-ssis-project-4");
        projectDir = Path.GetFullPath(projectDir);

        if (!Directory.Exists(projectDir))
            return;

        var parser = new SsisPackageParser(projectDir);
        var pkgPath = Path.Combine(projectDir, "Pkg_01_Extract_CustomerOrders.dtsx");
        var graph = parser.Parse(pkgPath);

        // Generate Python Pandas extraction script
        var result = SsisMigrationConverter.ConvertProject(graph, MigrationTarget.PythonPandas);
        Assert.NotEmpty(result.Files);

        var pyFile = result.Files.First();
        Assert.Contains("import pyodbc", pyFile.Content);
        Assert.Contains("import pandas", pyFile.Content);
        Assert.Contains("import numpy as np", pyFile.Content);
        Assert.Contains("extract_and_load", pyFile.Content);
        Assert.Contains("map_dtype", pyFile.Content);
        Assert.Contains("clean_val", pyFile.Content);

        // Verify improved map_dtype with bool and datetime64 handling
        Assert.Contains("'bool'", pyFile.Content);
        Assert.Contains("'datetime64'", pyFile.Content);

        // Verify numpy type handling in clean_val
        Assert.Contains("np.integer", pyFile.Content);
        Assert.Contains("np.floating", pyFile.Content);
        Assert.Contains("np.bool_", pyFile.Content);

        Console.WriteLine("=== Generated Python Script for Pkg_01_Extract_CustomerOrders ===");
        Console.WriteLine(pyFile.Content);
        Console.WriteLine("=== END ===");
    }

    [Fact]
    public void ExportToTemplateZip_GeneratesValidDbtModelsInZip()
    {
        var projectDir = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
            "..", "..", "..", "..", "..", "sample-ssis-project-4");
        projectDir = Path.GetFullPath(projectDir);

        if (!Directory.Exists(projectDir))
            return;

        var parser = new SsisPackageParser(projectDir);
        var pkgPath = Path.Combine(projectDir, "Pkg_01_Extract_CustomerOrders.dtsx");
        var graph = parser.Parse(pkgPath);

        var zipBytes = ProjectExportService.ExportToTemplateZip(graph, "");
        Assert.NotEmpty(zipBytes);

        using var ms = new MemoryStream(zipBytes);
        using var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);

        var entry = archive.GetEntry("dags/dbt_project/models/stg_01_extract_customerorders.sql");
        Assert.NotNull(entry);

        using var reader = new StreamReader(entry.Open());
        var content = reader.ReadToEnd();

        Assert.Contains("lookup_0.CustomerName", content);
        Assert.Contains("lookup_0.CustomerSegment", content);
        Assert.DoesNotContain("source_data.CustomerName", content);
    }

    [Fact]
    public void SampleProject4_FullProject_ValidationTest()
    {
        var projectDir = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
            "..", "..", "..", "..", "..", "sample-ssis-project-4");
        projectDir = Path.GetFullPath(projectDir);

        if (!Directory.Exists(projectDir))
            return;

        var parser = new SsisPackageParser(projectDir);
        var pkgFiles = Directory.GetFiles(projectDir, "*.dtsx");
        var graph = parser.ParseMultiple(pkgFiles);

        var result = SsisMigrationConverter.ConvertProject(graph, MigrationTarget.DbtSql);

        Assert.True(result.IsValid, $"Validation failed with errors: {string.Join("; ", result.ValidationErrors)}");
    }
}
