using System.Reflection;
using SsisLineage.Core;
using SsisLineage.Core.Models;

namespace SsisLineage.Tests;

public class SsisMigrationConverterTests
{
    private static string TranslateExpr(string ssisExpr)
    {
        var method = typeof(SsisMigrationConverter).GetMethod(
            "TranslateSsisExpressionToSql",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (string)method!.Invoke(null, new object[] { ssisExpr })!;
    }

    [Fact]
    public void DoubleQuotedStrings_ConvertedToSingleQuotes()
    {
        var result = TranslateExpr("[IsHighValue] == 1 ? \"TIER_GOLD\" : \"TIER_STANDARD\"");
        // Should produce: CASE WHEN [IsHighValue] = 1 THEN 'TIER_GOLD' ELSE 'TIER_STANDARD' END
        Assert.Contains("'TIER_GOLD'", result);
        Assert.Contains("'TIER_STANDARD'", result);
        Assert.DoesNotContain("\"TIER_GOLD\"", result);
        Assert.Contains("CASE WHEN", result);
        Assert.Contains("END", result);
    }

    [Fact]
    public void TernaryOperator_ConvertedToCaseWhen()
    {
        var result = TranslateExpr("[OrderAmount] > 1000 ? 1 : 0");
        Assert.Contains("CASE WHEN", result);
        Assert.Contains("THEN 1", result);
        Assert.Contains("ELSE 0", result);
        Assert.Contains("END", result);
    }

    [Fact]
    public void NestedTernary_ConvertedCorrectly()
    {
        var result = TranslateExpr("[Score] >= 75 ? \"A\" : ([Score] >= 40 ? \"B\" : \"C\")");
        Assert.Contains("'A'", result);
        Assert.Contains("'B'", result);
        Assert.Contains("'C'", result);
        Assert.DoesNotContain("\"A\"", result);
        // Should have two nested CASE WHEN
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(result, "CASE WHEN").Count);
    }

    [Fact]
    public void EqualityOperator_ConvertedToSingleEquals()
    {
        var result = TranslateExpr("[Status] == 1");
        Assert.Contains("= 1", result);
        Assert.DoesNotContain("==", result);
    }

    [Fact]
    public void InequalityOperator_ConvertedToDiamondBrackets()
    {
        var result = TranslateExpr("[Status] != 0");
        Assert.Contains("<>", result);
        Assert.DoesNotContain("!=", result);
    }

    [Fact]
    public void LogicalAnd_ConvertedToSqlAnd()
    {
        var result = TranslateExpr("[A] == 1 && [B] == 2");
        Assert.Contains("AND", result);
        Assert.DoesNotContain("&&", result);
    }

    [Fact]
    public void LogicalOr_ConvertedToSqlOr()
    {
        var result = TranslateExpr("[A] == 1 || [B] == 2");
        Assert.Contains("OR", result);
        Assert.DoesNotContain("||", result);
    }

    [Fact]
    public void IsNull_ConvertedToSqlIsNull()
    {
        var result = TranslateExpr("ISNULL([Name])");
        Assert.Contains("IS NULL", result);
        Assert.DoesNotContain("ISNULL(", result);
    }

    [Fact]
    public void NotIsNull_ConvertedToSqlIsNotNull()
    {
        var result = TranslateExpr("!ISNULL([Name])");
        Assert.Contains("IS NOT NULL", result);
    }

    [Fact]
    public void DtTypeStripping_RemovesAllDtTypes()
    {
        var result = TranslateExpr("(DT_WSTR, 50)(DT_I4)(DT_R8)(DT_BOOL)(DT_DATE)(DT_CY)(DT_GUID)[Col]");
        Assert.DoesNotContain("DT_", result);
        Assert.Contains("[Col]", result);
    }

    [Fact]
    public void TrimFunction_ConvertedToLtrimRtrim()
    {
        var result = TranslateExpr("TRIM([Name])");
        Assert.Contains("LTRIM(RTRIM(", result);
        Assert.DoesNotContain("TRIM(", result.Replace("LTRIM", "").Replace("RTRIM", ""));
    }

    [Fact]
    public void SimpleMathExpression_PreservedCorrectly()
    {
        var result = TranslateExpr("[OrderAmount] * (1 - [DiscountRate])");
        Assert.Contains("[OrderAmount]", result);
        Assert.Contains("(1 - [DiscountRate])", result);
    }

    [Fact]
    public void EndToEnd_DbtModelGeneration_ProducesValidSql()
    {
        // Build a minimal lineage graph representing the Pkg_02_Transform_OrderSummary scenario
        var graph = new LineageGraph();
        var pkg = new PackageNode
        {
            Id = "pkg-02",
            Name = "Pkg_02_Transform_OrderSummary",
            Path = "test.dtsx",
            ProjectPath = "."
        };
        graph.Packages.Add(pkg);

        // Source component (no lookup, no destination)
        graph.Components.Add(new ComponentNode
        {
            Id = "pkg-02::src",
            Name = "OLE DB Source - Staging",
            Type = "OLE DB Source",
            PackageId = "pkg-02",
            TaskId = "task-02",
            SqlQueryOrTable = "dbo.dbo_stg_CustomerOrders"
        });

        // Derived column component
        graph.Components.Add(new ComponentNode
        {
            Id = "pkg-02::derived",
            Name = "Derived Column Summary",
            Type = "Derived Column",
            PackageId = "pkg-02",
            TaskId = "task-02"
        });

        // Column mappings — derived column expression with double-quoted strings
        graph.ColumnMappings.Add(new ColumnMap
        {
            PackageId = "pkg-02",
            TaskId = "task-02",
            SourceComponentId = "pkg-02::derived",
            SourceComponentName = "Derived Column Summary",
            SourceColumnName = "VipBonusTier",
            SourceExpression = "[IsHighValue] == 1 ? \"TIER_GOLD\" : \"TIER_STANDARD\"",
            TargetComponentId = "pkg-02::derived",
            TargetComponentName = "Derived Column Summary",
            TargetColumnName = "VipBonusTier",
            OperationType = "DERIVED_COLUMN"
        });

        // Generate dbt model
        var result = SsisMigrationConverter.ConvertProject(graph, MigrationTarget.DbtSql);
        Assert.True(result.Files.Count >= 2, "Should produce schema.yml + at least 1 model");

        var modelFile = result.Files.FirstOrDefault(f => f.FileName.Contains("ordersummary"));
        Assert.NotNull(modelFile);

        // Verify the generated SQL contains single-quoted strings, not double-quoted
        Assert.Contains("'TIER_GOLD'", modelFile.Content);
        Assert.Contains("'TIER_STANDARD'", modelFile.Content);
        Assert.DoesNotContain("\"TIER_GOLD\"", modelFile.Content);
        Assert.DoesNotContain("\"TIER_STANDARD\"", modelFile.Content);

        // Verify CASE WHEN structure
        Assert.Contains("CASE WHEN", modelFile.Content);
        Assert.Contains("END", modelFile.Content);
        Assert.Contains("AS VipBonusTier", modelFile.Content);
    }

    [Fact]
    public void PlainTableName_WrappedInSelectStar()
    {
        var graph = new LineageGraph();
        var pkg = new PackageNode { Id = "pkg-01", Name = "Pkg_01_Extract", Path = "test.dtsx", ProjectPath = "." };
        graph.Packages.Add(pkg);

        graph.Components.Add(new ComponentNode
        {
            Id = "pkg-01::src",
            Name = "OLE DB Source",
            Type = "OLE DB Source",
            PackageId = "pkg-01",
            TaskId = "task-01",
            SqlQueryOrTable = "dbo.Orders"
        });

        graph.Components.Add(new ComponentNode
        {
            Id = "pkg-01::lkp",
            Name = "Lookup Customers",
            Type = "Lookup",
            PackageId = "pkg-01",
            TaskId = "task-01",
            SqlQueryOrTable = "dbo.Customers"
        });

        // Test Python generation
        var pyResult = SsisMigrationConverter.ConvertProject(graph, MigrationTarget.PythonPandas);
        var pyFile = pyResult.Files.FirstOrDefault(f => f.FileName.EndsWith(".py"));
        Assert.NotNull(pyFile);
        Assert.Contains("SELECT * FROM dbo.Orders", pyFile.Content);
        Assert.DoesNotContain("extract_query = \"\"\"\n        dbo.Orders", pyFile.Content);

        // Test dbt generation
        var dbtResult = SsisMigrationConverter.ConvertProject(graph, MigrationTarget.DbtSql);
        var dbtFile = dbtResult.Files.FirstOrDefault(f => f.FileName.EndsWith(".sql"));
        Assert.NotNull(dbtFile);
        Assert.Contains("SELECT * FROM dbo.Customers", dbtFile.Content);
        Assert.True(dbtResult.IsValid);
        Assert.Contains("QUALITY GATE: All generated artifacts passed validation!", dbtResult.Summary);
    }
}
