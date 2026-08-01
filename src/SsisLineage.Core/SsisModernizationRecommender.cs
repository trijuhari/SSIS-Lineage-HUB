using System;
using System.Linq;
using SsisLineage.Core.Models;

namespace SsisLineage.Core
{
    public static class SsisModernizationRecommender
    {
        public static void EnrichGraphWithRecommendations(LineageGraph graph)
        {
            if (graph == null) return;

            foreach (var task in graph.Tasks)
            {
                task.ModernizationRecommendation = GetTaskRecommendation(task.Type);
            }

            foreach (var comp in graph.Components)
            {
                comp.ModernizationRecommendation = GetComponentRecommendation(comp.Type);
            }
            
            // Also execution edges represent Control Flow precedence constraints
            // We can handle them in UI mapping.
        }

        private static string GetTaskRecommendation(string type)
        {
            if (string.IsNullOrWhiteSpace(type)) return "";

            var t = type.ToLowerInvariant();
            if (t.Contains("executesqltask") || t.Contains("execute sql task"))
                return "dbt pre/post-hook or PythonOperator";
            if (t.Contains("pipelinetask") || t.Contains("data flow"))
                return "dbt Model (SQL)";
            if (t.Contains("executepackagetask") || t.Contains("execute package"))
                return "Airflow SubDAG or TriggerDagRunOperator";
            if (t.Contains("scripttask") || t.Contains("script task"))
                return "PythonOperator";
            if (t.Contains("sendmailtask") || t.Contains("send mail"))
                return "Airflow EmailOperator";
                
            return "Airflow Operator";
        }

        private static string GetComponentRecommendation(string type)
        {
            if (string.IsNullOrWhiteSpace(type)) return "";

            var t = type.ToLowerInvariant();
            if (t.Contains("source"))
                return "Python Extraction (pyodbc/pandas)";
            if (t.Contains("destination"))
                return "Python Load (to Landing) or dbt Materialization";
            if (t.Contains("derived column") || t.Contains("lookup") || t.Contains("conditional split") || t.Contains("aggregate"))
                return "dbt Model (SQL)";
            if (t.Contains("sort"))
                return "dbt Model (ORDER BY)";
            if (t.Contains("union all"))
                return "dbt Model (UNION ALL)";
            if (t.Contains("script component"))
                return "Python / PySpark";
                
            return "dbt Transformation";
        }
    }
}
