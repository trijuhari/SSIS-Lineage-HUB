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
                return $"Executes a child package to process a sub-workflow.";
            }

            if (task.Type.Contains("Sequence", StringComparison.OrdinalIgnoreCase))
            {
                return $"Sequence Container: Groups multiple workflow tasks to be executed together.";
            }

            if (task.Type.Contains("ForEachLoop", StringComparison.OrdinalIgnoreCase) || task.Type.Contains("ForLoop", StringComparison.OrdinalIgnoreCase))
            {
                return $"Performs a looping operation to process a dataset iteratively.";
            }

            if (!string.IsNullOrEmpty(task.Description))
            {
                return task.Description;
            }

            return $"Executes the task '{task.Name}' (Type: {task.Type}).";
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
                var srcText = !string.IsNullOrEmpty(translatedSqlOrTable) ? $" from {translatedSqlOrTable}" : "";
                return $"Extracts initial data (Source){srcText} to be processed in the data flow.";
            }

            if (type.Contains("Destination", StringComparison.OrdinalIgnoreCase))
            {
                var destText = !string.IsNullOrEmpty(translatedSqlOrTable) ? $" into {translatedSqlOrTable}" : "";
                return $"Loads the processed data results (Destination){destText}.";
            }

            if (type.Contains("Lookup", StringComparison.OrdinalIgnoreCase))
            {
                var refText = !string.IsNullOrEmpty(translatedSqlOrTable) ? $" against {translatedSqlOrTable}" : "";
                return $"Looks up reference data{refText} based on key columns.";
            }

            if (type.Contains("Derived", StringComparison.OrdinalIgnoreCase) || type.Contains("Derived Column", StringComparison.OrdinalIgnoreCase))
            {
                var cols = compMappings.Select(m => m.TargetColumnName).Distinct().ToList();
                var colText = cols.Count > 0 ? $" ({string.Join(", ", cols.Select(c => glossary.Translate(c, false)))})" : "";
                return $"Creates or updates columns (Derived Column){colText} using business logic expressions.";
            }

            if (type.Contains("Aggregate", StringComparison.OrdinalIgnoreCase))
            {
                var groupCols = compMappings.Where(m => string.IsNullOrEmpty(m.SourceExpression)).Select(m => m.TargetColumnName).Distinct().ToList();
                var groupText = groupCols.Count > 0 ? $" grouped by {string.Join(", ", groupCols.Select(c => glossary.Translate(c, false)))}" : "";
                return $"Performs data aggregation (e.g., sum, count, or avg){groupText}.";
            }

            if (type.Contains("Union", StringComparison.OrdinalIgnoreCase))
            {
                return "Combines multiple data streams into a single dataset (Union All).";
            }

            if (type.Contains("Sort", StringComparison.OrdinalIgnoreCase))
            {
                return "Sorts data based on specific columns.";
            }

            if (type.Contains("Filter", StringComparison.OrdinalIgnoreCase) || type.Contains("Split", StringComparison.OrdinalIgnoreCase))
            {
                return "Filters or routes the data stream (Conditional Split) based on specific conditions.";
            }

            if (type.Equals("Execute SQL Task", StringComparison.OrdinalIgnoreCase))
            {
                var taskDummy = new TaskNode { Type = "ExecuteSQLTask", Name = component.Name };
                return GenerateSqlTaskNarrative(taskDummy, compMappings, glossary);
            }

            if (!string.IsNullOrEmpty(translatedSqlOrTable))
            {
                return $"Processes data related to {translatedSqlOrTable} (Type: {type}).";
            }

            return $"Performs transformation '{component.Name}' (Type: {type}).";
        }

        private static string GenerateSqlTaskNarrative(TaskNode task, List<ColumnMap> mappings, BusinessGlossary glossary)
        {
            if (mappings.Count == 0)
            {
                // Fallback if no column mappings are generated
                return $"Executes a SQL query to process data in the database.";
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
                sb.Append("Merges data");
                if (!string.IsNullOrEmpty(primarySource))
                {
                    sb.Append($" from {glossary.Translate(primarySource)}");
                }
                if (targetTables.Count > 0)
                {
                    sb.Append($" into {glossary.Translate(targetTables[0])}");
                }
                if (joinDetails.Count > 0)
                {
                    var cleanJoins = string.Join(", ", joinDetails.Select(j => glossary.TranslateExpression(j)));
                    sb.Append($" based on condition: {cleanJoins}");
                }
                sb.Append(". Performs an INSERT for new data, or an UPDATE for existing data.");
            }
            else if (isDelete)
            {
                sb.Append("Deletes data");
                if (targetTables.Count > 0)
                {
                    sb.Append($" from {glossary.Translate(targetTables[0])}");
                }
                if (joinedTables.Count > 0)
                {
                    sb.Append($" joining with tables {string.Join(", ", joinedTables.Select(t => glossary.Translate(t)))}");
                }
                if (filterConditions.Count > 0)
                {
                    var cleanFilters = string.Join(" AND ", filterConditions.Select(f => glossary.TranslateExpression(f)));
                    sb.Append($" based on filter: {cleanFilters}");
                }
                sb.Append(".");
            }
            else if (isUpdate)
            {
                sb.Append("Updates data");
                if (targetTables.Count > 0)
                {
                    sb.Append($" in {glossary.Translate(targetTables[0])}");
                }
                if (!string.IsNullOrEmpty(primarySource))
                {
                    sb.Append($" using data from {glossary.Translate(primarySource)}");
                }
                if (joinedTables.Count > 1) // more than just primary source
                {
                    var otherJoins = joinedTables.Where(t => !t.Equals(primarySource, StringComparison.OrdinalIgnoreCase));
                    sb.Append($", joined with {string.Join(", ", otherJoins.Select(t => glossary.Translate(t)))}");
                }
                if (joinDetails.Count > 0)
                {
                    var cleanJoins = string.Join(", ", joinDetails.Select(j => glossary.TranslateExpression(j)));
                    sb.Append($" based on {cleanJoins}");
                }
                if (filterConditions.Count > 0)
                {
                    var cleanFilters = string.Join(" AND ", filterConditions.Select(f => glossary.TranslateExpression(f)));
                    sb.Append($" with filter {cleanFilters}");
                }
                sb.Append(".");
            }
            else // Insert or Select Into or standard extraction
            {
                sb.Append("Extracts data");
                if (!string.IsNullOrEmpty(primarySource))
                {
                    sb.Append($" from {glossary.Translate(primarySource)}");
                }
                else if (sourceTables.Count > 0)
                {
                    sb.Append($" from {glossary.Translate(sourceTables[0])}");
                }

                if (joinedTables.Count > 0)
                {
                    // Filter out primary source
                    var others = joinedTables.Where(t => !t.Equals(primarySource, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (others.Count > 0)
                    {
                        sb.Append($", joined with {string.Join(", ", others.Select(t => glossary.Translate(t)))}");
                    }
                }

                if (joinDetails.Count > 0)
                {
                    var cleanJoins = string.Join(", ", joinDetails.Select(j => glossary.TranslateExpression(j)));
                    sb.Append($" based on {cleanJoins}");
                }

                if (filterConditions.Count > 0)
                {
                    var cleanFilters = string.Join(" AND ", filterConditions.Select(f => glossary.TranslateExpression(f)));
                    sb.Append($" with filter {cleanFilters}");
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
                        sb.Append($", then aggregated by {string.Join(", ", groupCols.Select(c => glossary.Translate(c, false)))}");
                    }
                    else
                    {
                        sb.Append(", then aggregated");
                    }
                }

                if (targetTables.Count > 0)
                {
                    sb.Append($", loading the results into {glossary.Translate(targetTables[0])}");
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
            sb.Append("Data Flow: ");
            
            if (srcNames.Count > 0)
            {
                sb.Append($"Extracts data from {string.Join(", ", srcNames.Select(s => glossary.Translate(s)))}");
            }
            else
            {
                sb.Append("Extracts data");
            }

            // Mentions transformation kinds
            var transforms = components.Where(c => !c.Type.Contains("Source", StringComparison.OrdinalIgnoreCase) && 
                                                   !c.Type.Contains("Destination", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Type).Distinct().ToList();

            if (transforms.Count > 0)
            {
                var cleanTransforms = transforms.Select(t => t.Replace(" Component", "").Replace(" Transformation", "")).ToList();
                sb.Append($", processes it through {string.Join(", ", cleanTransforms)} transformations");
            }

            if (destNames.Count > 0)
            {
                sb.Append($", and loads the results into {string.Join(", ", destNames.Select(d => glossary.Translate(d)))}");
            }
            sb.Append(".");

            return sb.ToString();
        }
    }
}
