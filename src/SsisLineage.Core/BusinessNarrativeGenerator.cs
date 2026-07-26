using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SsisLineage.Core.Models;

namespace SsisLineage.Core
{
    public static class BusinessNarrativeGenerator
    {
        public static string GenerateTaskNarrative(TaskNode task, List<ColumnMap> taskMappings, List<ComponentNode> taskComponents, BusinessGlossary glossary)
        {
            if (task == null) return "";
            glossary ??= new BusinessGlossary();
            taskMappings ??= new List<ColumnMap>();
            taskComponents ??= new List<ComponentNode>();

            if (task.Type.Contains("ExecuteSQLTask", StringComparison.OrdinalIgnoreCase))
            {
                return GenerateSqlTaskNarrative(task, taskMappings, glossary);
            }

            if (task.Type.Contains("PipelineTask", StringComparison.OrdinalIgnoreCase) || task.Type.Contains("Pipeline", StringComparison.OrdinalIgnoreCase))
            {
                return GenerateDataFlowTaskNarrative(task, taskComponents, taskMappings, glossary);
            }

            if (task.Type.Contains("ExecutePackageTask", StringComparison.OrdinalIgnoreCase))
            {
                return $"Menjalankan package anak (Child Package) untuk memproses sub-alur kerja.";
            }

            if (task.Type.Contains("Sequence", StringComparison.OrdinalIgnoreCase))
            {
                return $"Wadah Sequence: Mengelompokkan beberapa task alur kerja untuk dieksekusi bersama.";
            }

            if (task.Type.Contains("ForEachLoop", StringComparison.OrdinalIgnoreCase) || task.Type.Contains("ForLoop", StringComparison.OrdinalIgnoreCase))
            {
                return $"Melakukan perulangan (Looping) untuk memproses kumpulan data secara berulang.";
            }

            if (!string.IsNullOrEmpty(task.Description))
            {
                return task.Description;
            }

            return $"Menjalankan task '{task.Name}' (Tipe: {task.Type}).";
        }

        public static string GenerateComponentNarrative(ComponentNode component, List<ColumnMap> compMappings, BusinessGlossary glossary)
        {
            if (component == null) return "";
            glossary ??= new BusinessGlossary();
            compMappings ??= new List<ColumnMap>();

            var type = component.Type ?? "";
            var sqlOrTable = component.SqlQueryOrTable ?? "";
            var translatedSqlOrTable = !string.IsNullOrEmpty(sqlOrTable) ? glossary.Translate(sqlOrTable) : "";

            if (type.Contains("Source", StringComparison.OrdinalIgnoreCase))
            {
                var srcText = !string.IsNullOrEmpty(translatedSqlOrTable) ? $" dari {translatedSqlOrTable}" : "";
                return $"Mengambil data awal (Source){srcText} untuk diproses dalam alur data.";
            }

            if (type.Contains("Destination", StringComparison.OrdinalIgnoreCase))
            {
                var destText = !string.IsNullOrEmpty(translatedSqlOrTable) ? $" ke {translatedSqlOrTable}" : "";
                return $"Memuat hasil pemrosesan data (Destination){destText}.";
            }

            if (type.Contains("Lookup", StringComparison.OrdinalIgnoreCase))
            {
                var refText = !string.IsNullOrEmpty(translatedSqlOrTable) ? $" dengan {translatedSqlOrTable}" : "";
                return $"Mencocokkan/mencari data referensi (Lookup){refText} berdasarkan kolom kunci.";
            }

            if (type.Contains("Derived", StringComparison.OrdinalIgnoreCase) || type.Contains("Derived Column", StringComparison.OrdinalIgnoreCase))
            {
                var cols = compMappings.Select(m => m.TargetColumnName).Distinct().ToList();
                var colText = cols.Count > 0 ? $" ({string.Join(", ", cols.Select(c => glossary.Translate(c, false)))})" : "";
                return $"Membuat atau memperbarui kolom baru (Derived Column){colText} menggunakan ekspresi logika bisnis.";
            }

            if (type.Contains("Aggregate", StringComparison.OrdinalIgnoreCase))
            {
                var groupCols = compMappings.Where(m => string.IsNullOrEmpty(m.SourceExpression)).Select(m => m.TargetColumnName).Distinct().ToList();
                var groupText = groupCols.Count > 0 ? $" berdasarkan {string.Join(", ", groupCols.Select(c => glossary.Translate(c, false)))}" : "";
                return $"Melakukan agregasi data (seperti sum, count, atau avg){groupText}.";
            }

            if (type.Contains("Union", StringComparison.OrdinalIgnoreCase))
            {
                return "Menggabungkan beberapa aliran data menjadi satu aliran data tunggal (Union All).";
            }

            if (type.Contains("Sort", StringComparison.OrdinalIgnoreCase))
            {
                return "Mengurutkan data berdasarkan kolom tertentu.";
            }

            if (type.Contains("Filter", StringComparison.OrdinalIgnoreCase) || type.Contains("Split", StringComparison.OrdinalIgnoreCase))
            {
                return "Memfilter atau membagi aliran data (Conditional Split) berdasarkan kondisi tertentu.";
            }

            if (type.Equals("Execute SQL Task", StringComparison.OrdinalIgnoreCase))
            {
                var taskDummy = new TaskNode { Type = "ExecuteSQLTask", Name = component.Name };
                return GenerateSqlTaskNarrative(taskDummy, compMappings, glossary);
            }

            if (!string.IsNullOrEmpty(translatedSqlOrTable))
            {
                return $"Memproses data terkait {translatedSqlOrTable} (Tipe: {type}).";
            }

            return $"Melakukan transformasi '{component.Name}' (Tipe: {type}).";
        }

        private static string GenerateSqlTaskNarrative(TaskNode task, List<ColumnMap> mappings, BusinessGlossary glossary)
        {
            if (mappings.Count == 0)
            {
                // Fallback if no column mappings are generated
                return $"Menjalankan query SQL untuk memproses data pada database.";
            }

            var opTypes = mappings.Select(m => m.OperationType).Distinct().ToList();
            var isDelete = opTypes.Any(o => o.Contains("DELETE", StringComparison.OrdinalIgnoreCase));
            var isUpdate = opTypes.Any(o => o.Contains("UPDATE", StringComparison.OrdinalIgnoreCase));
            var isMerge = opTypes.Any(o => o.Contains("MERGE", StringComparison.OrdinalIgnoreCase));

            // Extract table names
            var sourceTables = mappings.Select(m => m.SourceTable).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();
            var targetTables = mappings.Select(m => m.TargetTable).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();

            // Filter out target table from source tables (in joins)
            var joinedTables = sourceTables.Where(s => !targetTables.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
            var primarySource = sourceTables.FirstOrDefault(s => targetTables.Count == 0 || !s.Equals(targetTables[0], StringComparison.OrdinalIgnoreCase)) ?? "";

            // Join details
            var joinDetails = mappings.Select(m => m.JoinDetails).Where(j => !string.IsNullOrEmpty(j)).Distinct().ToList();
            // Filter conditions
            var filterConditions = mappings.Select(m => m.FilterConditions).Where(f => !string.IsNullOrEmpty(f)).Distinct().ToList();

            var sb = new StringBuilder();

            if (isMerge)
            {
                sb.Append("Menggabungkan (Merge) data");
                if (!string.IsNullOrEmpty(primarySource))
                {
                    sb.Append($" dari {glossary.Translate(primarySource)}");
                }
                if (targetTables.Count > 0)
                {
                    sb.Append($" ke dalam {glossary.Translate(targetTables[0])}");
                }
                if (joinDetails.Count > 0)
                {
                    var cleanJoins = string.Join(", ", joinDetails.Select(j => glossary.TranslateExpression(j)));
                    sb.Append($" berdasarkan kondisi: {cleanJoins}");
                }
                sb.Append(". Melakukan penambahan (INSERT) jika data baru, atau pembaruan (UPDATE) jika data sudah ada.");
            }
            else if (isDelete)
            {
                sb.Append("Menghapus data");
                if (targetTables.Count > 0)
                {
                    sb.Append($" dari {glossary.Translate(targetTables[0])}");
                }
                if (joinedTables.Count > 0)
                {
                    sb.Append($" dengan melibatkan tabel {string.Join(", ", joinedTables.Select(t => glossary.Translate(t)))}");
                }
                if (filterConditions.Count > 0)
                {
                    var cleanFilters = string.Join(" AND ", filterConditions.Select(f => glossary.TranslateExpression(f)));
                    sb.Append($" berdasarkan filter: {cleanFilters}");
                }
                sb.Append(".");
            }
            else if (isUpdate)
            {
                sb.Append("Memperbarui data");
                if (targetTables.Count > 0)
                {
                    sb.Append($" pada {glossary.Translate(targetTables[0])}");
                }
                if (!string.IsNullOrEmpty(primarySource))
                {
                    sb.Append($" menggunakan data dari {glossary.Translate(primarySource)}");
                }
                if (joinedTables.Count > 1) // more than just primary source
                {
                    var otherJoins = joinedTables.Where(t => !t.Equals(primarySource, StringComparison.OrdinalIgnoreCase));
                    sb.Append($", digabung dengan {string.Join(", ", otherJoins.Select(t => glossary.Translate(t)))}");
                }
                if (joinDetails.Count > 0)
                {
                    var cleanJoins = string.Join(", ", joinDetails.Select(j => glossary.TranslateExpression(j)));
                    sb.Append($" berdasarkan {cleanJoins}");
                }
                if (filterConditions.Count > 0)
                {
                    var cleanFilters = string.Join(" AND ", filterConditions.Select(f => glossary.TranslateExpression(f)));
                    sb.Append($" dengan filter {cleanFilters}");
                }
                sb.Append(".");
            }
            else // Insert or Select Into or standard extraction
            {
                sb.Append("Mengambil data");
                if (!string.IsNullOrEmpty(primarySource))
                {
                    sb.Append($" dari {glossary.Translate(primarySource)}");
                }
                else if (sourceTables.Count > 0)
                {
                    sb.Append($" dari {glossary.Translate(sourceTables[0])}");
                }

                if (joinedTables.Count > 0)
                {
                    // Filter out primary source
                    var others = joinedTables.Where(t => !t.Equals(primarySource, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (others.Count > 0)
                    {
                        sb.Append($", digabung dengan {string.Join(", ", others.Select(t => glossary.Translate(t)))}");
                    }
                }

                if (joinDetails.Count > 0)
                {
                    var cleanJoins = string.Join(", ", joinDetails.Select(j => glossary.TranslateExpression(j)));
                    sb.Append($" berdasarkan {cleanJoins}");
                }

                if (filterConditions.Count > 0)
                {
                    var cleanFilters = string.Join(" AND ", filterConditions.Select(f => glossary.TranslateExpression(f)));
                    sb.Append($" dengan filter {cleanFilters}");
                }

                // Check if aggregate
                var hasAgg = mappings.Any(m => m.SourceExpression != null && 
                    (m.SourceExpression.Contains("SUM(", StringComparison.OrdinalIgnoreCase) ||
                     m.SourceExpression.Contains("COUNT(", StringComparison.OrdinalIgnoreCase) ||
                     m.SourceExpression.Contains("AVG(", StringComparison.OrdinalIgnoreCase) ||
                     m.SourceExpression.Contains("MAX(", StringComparison.OrdinalIgnoreCase) ||
                     m.SourceExpression.Contains("MIN(", StringComparison.OrdinalIgnoreCase)));
                
                if (hasAgg)
                {
                    var groupCols = mappings.Where(m => string.IsNullOrEmpty(m.SourceExpression) || !m.SourceExpression.Contains("("))
                        .Select(m => m.TargetColumnName).Distinct().ToList();
                    if (groupCols.Count > 0)
                    {
                        sb.Append($", lalu diagregasi per {string.Join(", ", groupCols.Select(c => glossary.Translate(c, false)))}");
                    }
                    else
                    {
                        sb.Append(", lalu diagregasi");
                    }
                }

                if (targetTables.Count > 0)
                {
                    sb.Append($", hasilnya dimuat ke {glossary.Translate(targetTables[0])}");
                }
                sb.Append(".");
            }

            return sb.ToString();
        }

        private static string GenerateDataFlowTaskNarrative(TaskNode task, List<ComponentNode> components, List<ColumnMap> mappings, BusinessGlossary glossary)
        {
            var sources = components.Where(c => c.Type.Contains("Source", StringComparison.OrdinalIgnoreCase)).ToList();
            var destinations = components.Where(c => c.Type.Contains("Destination", StringComparison.OrdinalIgnoreCase)).ToList();
            
            var srcNames = sources.Select(s => string.IsNullOrEmpty(s.SqlQueryOrTable) ? s.Name : s.SqlQueryOrTable)
                .Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
            var destNames = destinations.Select(d => string.IsNullOrEmpty(d.SqlQueryOrTable) ? d.Name : d.SqlQueryOrTable)
                .Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();

            var sb = new StringBuilder();
            sb.Append("Aliran Data (Data Flow): ");
            
            if (srcNames.Count > 0)
            {
                sb.Append($"Mengambil data dari {string.Join(", ", srcNames.Select(s => glossary.Translate(s)))}");
            }
            else
            {
                sb.Append("Mengambil data");
            }

            // Mentions transformation kinds
            var transforms = components.Where(c => !c.Type.Contains("Source", StringComparison.OrdinalIgnoreCase) && 
                                                   !c.Type.Contains("Destination", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Type).Distinct().ToList();

            if (transforms.Count > 0)
            {
                var cleanTransforms = transforms.Select(t => t.Replace(" Component", "").Replace(" Transformation", "")).ToList();
                sb.Append($", diproses melalui transformasi {string.Join(", ", cleanTransforms)}");
            }

            if (destNames.Count > 0)
            {
                sb.Append($", dan memuat hasilnya ke {string.Join(", ", destNames.Select(d => glossary.Translate(d)))}");
            }
            sb.Append(".");

            return sb.ToString();
        }
    }
}
