using Xunit;
using SsisLineage.Core;
using SsisLineage.Core.Models;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SsisLineage.Tests
{
    public class BusinessNarrativeAndGlossaryTests
    {
        [Fact]
        public void BusinessGlossary_ShouldTranslateDefaultTerms()
        {
            var glossary = BusinessGlossary.Load("");
            Assert.Equal("`Transaksi`", glossary.Translate("TRX", false));
            Assert.Equal("`Pelanggan`", glossary.Translate("CUST", false));
            Assert.Equal("`Transaksi Identitas`", glossary.Translate("TRX_ID", false));
        }

        [Fact]
        public void BusinessGlossary_ShouldLoadFromFile()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tempDir);
            try
            {
                var dict = new Dictionary<string, string>
                {
                    { "CUSTOM_KEY", "Kunci Kustom" },
                    { "TRX", "Transaksi Kustom" }
                };
                File.WriteAllText(Path.Combine(tempDir, "glossary.json"), JsonSerializer.Serialize(dict));

                var glossary = BusinessGlossary.Load(tempDir);
                Assert.Equal("`Kunci Kustom`", glossary.Translate("CUSTOM_KEY", false));
                Assert.Equal("`Transaksi Kustom`", glossary.Translate("TRX", false)); // overridden
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void BusinessGlossary_ShouldTranslateLogicalExpressions()
        {
            var glossary = BusinessGlossary.Load("");
            
            // Expression translation with boundaries
            Assert.Equal("Transaksi (TRX) = 'A'", glossary.TranslateExpression("TRX = 'A'"));
            Assert.Equal("Pelanggan (CUST) dan Transaksi (TRX)", glossary.TranslateExpression("CUST dan TRX"));
        }

        [Fact]
        public void BusinessNarrativeGenerator_ShouldGenerateDataFlowTaskNarrative()
        {
            var glossary = BusinessGlossary.Load("");
            var task = new TaskNode
            {
                Id = "T1",
                Name = "DFT_Load_Trx",
                Type = "STOCK:PipelineTask"
            };

            var mappings = new List<ColumnMap>
            {
                new ColumnMap { SourceColumnName = "trx_id", TargetColumnName = "TrxId", SourceComponentName = "Src_Trx", TargetComponentName = "Dest_Trx" }
            };

            var components = new List<ComponentNode>
            {
                new ComponentNode { Id = "C1", TaskId = "T1", Name = "Src_Trx", Type = "OLEDB Source" },
                new ComponentNode { Id = "C2", TaskId = "T1", Name = "Dest_Trx", Type = "OLEDB Destination" }
            };

            var narrative = BusinessNarrativeGenerator.GenerateTaskNarrative(task, mappings, components, glossary);
            
            Assert.Contains("Data Flow:", narrative);
            Assert.Contains("Extracts data", narrative);
            Assert.Contains("Src_Trx", narrative);
            Assert.Contains("Dest_Trx", narrative);
        }

        [Fact]
        public void BusinessNarrativeGenerator_ShouldGenerateExecuteSqlNarrative()
        {
            var glossary = BusinessGlossary.Load("");
            
            var taskInsert = new TaskNode
            {
                Id = "T2",
                Name = "SQL_Insert_Cust",
                Type = "Microsoft.ExecuteSQLTask"
            };
            var mapsInsert = new List<ColumnMap>
            {
                new ColumnMap { TaskId = "T2", OperationType = "SQL_PROC_INSERT", TargetTable = "DimCustomer", TargetColumnName = "CustomerName" }
            };
            var components = new List<ComponentNode>();

            var narrativeInsert = BusinessNarrativeGenerator.GenerateTaskNarrative(taskInsert, mapsInsert, components, glossary);
            Assert.Contains("Extracts data", narrativeInsert);
            Assert.Contains("DimCustomer", narrativeInsert);

            var taskUpdate = new TaskNode
            {
                Id = "T3",
                Name = "SQL_Update_Cust",
                Type = "Microsoft.ExecuteSQLTask"
            };
            var mapsUpdate = new List<ColumnMap>
            {
                new ColumnMap { TaskId = "T3", OperationType = "SQL_PROC_UPDATE", TargetTable = "DimCustomer" }
            };
            var narrativeUpdate = BusinessNarrativeGenerator.GenerateTaskNarrative(taskUpdate, mapsUpdate, components, glossary);
            Assert.Contains("Updates data", narrativeUpdate);
        }

        [Fact]
        public void BusinessNarrativeGenerator_ShouldGenerateComponentNarrative()
        {
            var glossary = BusinessGlossary.Load("");
            var comp = new ComponentNode
            {
                Id = "C1",
                Name = "OLEDB_Dest_Trx",
                Type = "OLEDB Destination",
                SqlQueryOrTable = "FactTransaction"
            };
            var maps = new List<ColumnMap>
            {
                new ColumnMap { TargetColumnName = "TrxId", TargetTable = "FactTransaction" }
            };

            var narrative = BusinessNarrativeGenerator.GenerateComponentNarrative(comp, maps, glossary);
            Assert.Contains("Loads", narrative);
            Assert.Contains("FactTransaction", narrative);
        }
    }
}
