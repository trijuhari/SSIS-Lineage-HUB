using System;
using System.Collections.Generic;
using System.Linq;
using SsisLineage.Core.Models;

namespace SsisLineage.Core
{
    public class TaskDurationInfo
    {
        public string TaskId { get; set; } = "";
        public string TaskName { get; set; } = "";
        public string PackageId { get; set; } = "";
        public string PackageName { get; set; } = "";
        public double EstimatedDurationSeconds { get; set; }
        public bool IsOnCriticalPath { get; set; }
        public string SlaRiskLevel { get; set; } = "Low"; // Low, Medium, High
        public string BottleneckReason { get; set; } = "";
    }

    public class CriticalPathReport
    {
        public List<string> CriticalPathTaskIds { get; set; } = new();
        public List<string> CriticalPathPackageIds { get; set; } = new();
        public List<TaskDurationInfo> TaskMetrics { get; set; } = new();
        public double TotalEstimatedDurationSeconds { get; set; }
        public double RecommendedSlaThresholdSeconds { get; set; }
        public bool IsSlaRiskDetected { get; set; }
        public string Summary { get; set; } = "";
        public string CriticalPathFormattedSequence { get; set; } = "";
    }

    public static class CriticalPathAnalyzer
    {
        public static CriticalPathReport AnalyzeProject(LineageGraph graph)
        {
            var report = new CriticalPathReport();
            if (graph == null || !graph.Packages.Any()) return report;

            // 1. Calculate estimated duration for each task
            var taskDurations = new Dictionary<string, TaskDurationInfo>();

            foreach (var task in graph.Tasks)
            {
                var pkg = graph.Packages.FirstOrDefault(p => p.Id == task.PackageId);
                var pkgName = pkg?.Name ?? task.PackageName;

                double duration = EstimateTaskDuration(graph, task);
                var reason = GetBottleneckReason(graph, task);
                string riskLevel = duration >= 300 ? "High" : (duration >= 120 ? "Medium" : "Low");

                var info = new TaskDurationInfo
                {
                    TaskId = task.Id,
                    TaskName = task.Name,
                    PackageId = task.PackageId,
                    PackageName = pkgName,
                    EstimatedDurationSeconds = duration,
                    SlaRiskLevel = riskLevel,
                    BottleneckReason = reason
                };

                taskDurations[task.Id] = info;
                report.TaskMetrics.Add(info);
            }

            // If graph has no explicit tasks, evaluate at Package level
            if (!graph.Tasks.Any())
            {
                foreach (var pkg in graph.Packages)
                {
                    double duration = EstimatePackageDuration(graph, pkg);
                    var info = new TaskDurationInfo
                    {
                        TaskId = pkg.Id,
                        TaskName = pkg.Name,
                        PackageId = pkg.Id,
                        PackageName = pkg.Name,
                        EstimatedDurationSeconds = duration,
                        SlaRiskLevel = duration >= 300 ? "High" : (duration >= 120 ? "Medium" : "Low"),
                        BottleneckReason = "Package Data Flow transformation volume"
                    };
                    taskDurations[pkg.Id] = info;
                    report.TaskMetrics.Add(info);
                }
            }

            // 2. Build Adjacency List for DAG path analysis
            var nodeIds = taskDurations.Keys.ToList();
            var adj = new Dictionary<string, List<string>>();
            var inDegree = new Dictionary<string, int>();

            foreach (var id in nodeIds)
            {
                adj[id] = new List<string>();
                inDegree[id] = 0;
            }

            // Connect using ExecutionEdges
            foreach (var edge in graph.ExecutionEdges)
            {
                if (adj.ContainsKey(edge.FromTaskId) && adj.ContainsKey(edge.ToTaskId))
                {
                    adj[edge.FromTaskId].Add(edge.ToTaskId);
                    inDegree[edge.ToTaskId]++;
                }
            }

            // 3. Dynamic Programming Longest Path (Critical Path)
            var dist = new Dictionary<string, double>();
            var parent = new Dictionary<string, string?>();

            foreach (var id in nodeIds)
            {
                dist[id] = taskDurations[id].EstimatedDurationSeconds;
                parent[id] = null;
            }

            // Topological sort via Kahn's algorithm or DFS
            var queue = new Queue<string>(nodeIds.Where(id => inDegree[id] == 0));
            var topoOrder = new List<string>();

            while (queue.Any())
            {
                var u = queue.Dequeue();
                topoOrder.Add(u);

                foreach (var v in adj[u])
                {
                    if (dist[u] + taskDurations[v].EstimatedDurationSeconds > dist[v])
                    {
                        dist[v] = dist[u] + taskDurations[v].EstimatedDurationSeconds;
                        parent[v] = u;
                    }

                    inDegree[v]--;
                    if (inDegree[v] == 0)
                    {
                        queue.Enqueue(v);
                    }
                }
            }

            // Handle remaining nodes in case of cycles or disconnected components
            foreach (var id in nodeIds)
            {
                if (!topoOrder.Contains(id)) topoOrder.Add(id);
            }

            // 4. Extract Critical Path
            string endNode = nodeIds.OrderByDescending(id => dist[id]).FirstOrDefault() ?? "";
            double maxDist = endNode != "" ? dist[endNode] : 0;

            var pathNodes = new List<string>();
            var curr = endNode;
            while (!string.IsNullOrEmpty(curr))
            {
                pathNodes.Add(curr);
                curr = parent[curr];
            }
            pathNodes.Reverse();

            // 5. Populate Report
            report.CriticalPathTaskIds = pathNodes;
            report.TotalEstimatedDurationSeconds = maxDist;
            report.RecommendedSlaThresholdSeconds = Math.Ceiling(maxDist * 1.25); // 25% buffer
            report.IsSlaRiskDetected = maxDist > 600; // SLA Risk if batch > 10 mins

            var criticalPkgs = new HashSet<string>();
            var seqNames = new List<string>();

            foreach (var taskId in pathNodes)
            {
                if (taskDurations.TryGetValue(taskId, out var info))
                {
                    info.IsOnCriticalPath = true;
                    criticalPkgs.Add(info.PackageId);
                    seqNames.Add($"{info.TaskName} ({info.EstimatedDurationSeconds:F0}s)");
                }
            }

            report.CriticalPathPackageIds = criticalPkgs.ToList();
            report.CriticalPathFormattedSequence = string.Join(" ➔ ", seqNames);
            report.Summary = $"Critical Path identified with {pathNodes.Count} critical bottleneck tasks. Total estimated duration: {maxDist:F0} seconds (~{Math.Ceiling(maxDist / 60):F0} mins). Recommended SLA: {report.RecommendedSlaThresholdSeconds:F0}s.";

            return report;
        }

        private static double EstimateTaskDuration(LineageGraph graph, TaskNode task)
        {
            double baseDuration = 10; // Default 10 seconds per task

            var type = task.Type ?? "";
            if (type.Contains("Data Flow", StringComparison.OrdinalIgnoreCase))
            {
                baseDuration = 45;
                var components = graph.Components.Where(c => c.TaskId == task.Id || c.PackageId == task.PackageId).ToList();

                foreach (var c in components)
                {
                    var cType = c.Type ?? "";
                    if (cType.Contains("Sort", StringComparison.OrdinalIgnoreCase)) baseDuration += 60; // Blocking sort
                    else if (cType.Contains("Aggregate", StringComparison.OrdinalIgnoreCase)) baseDuration += 45;
                    else if (cType.Contains("Lookup", StringComparison.OrdinalIgnoreCase)) baseDuration += 25;
                    else if (cType.Contains("Source", StringComparison.OrdinalIgnoreCase)) baseDuration += 30;
                    else if (cType.Contains("Destination", StringComparison.OrdinalIgnoreCase)) baseDuration += 20;
                    else if (cType.Contains("Script", StringComparison.OrdinalIgnoreCase)) baseDuration += 40;
                }
            }
            else if (type.Contains("Execute SQL", StringComparison.OrdinalIgnoreCase))
            {
                baseDuration = 20;
                if (task.Description != null && task.Description.Contains("PROCEDURE", StringComparison.OrdinalIgnoreCase))
                {
                    baseDuration += 50;
                }
            }
            else if (type.Contains("Execute Package", StringComparison.OrdinalIgnoreCase))
            {
                baseDuration = 90; // Parent calling child package
            }
            else if (type.Contains("Script", StringComparison.OrdinalIgnoreCase))
            {
                baseDuration = 35;
            }

            return baseDuration;
        }

        private static double EstimatePackageDuration(LineageGraph graph, PackageNode pkg)
        {
            var comps = graph.Components.Where(c => c.PackageId == pkg.Id).ToList();
            double dur = 30;
            foreach (var c in comps)
            {
                var cType = c.Type ?? "";
                if (cType.Contains("Sort", StringComparison.OrdinalIgnoreCase)) dur += 60;
                else if (cType.Contains("Aggregate", StringComparison.OrdinalIgnoreCase)) dur += 45;
                else if (cType.Contains("Lookup", StringComparison.OrdinalIgnoreCase)) dur += 25;
                else dur += 15;
            }
            return dur;
        }

        private static string GetBottleneckReason(LineageGraph graph, TaskNode task)
        {
            var comps = graph.Components.Where(c => c.TaskId == task.Id || c.PackageId == task.PackageId).ToList();

            if (comps.Any(c => c.Type.Contains("Sort", StringComparison.OrdinalIgnoreCase)))
            {
                return "PERF-001: In-memory blocking Sort transformation";
            }
            if (comps.Any(c => c.Type.Contains("Aggregate", StringComparison.OrdinalIgnoreCase)))
            {
                return "PERF-001: In-memory Aggregate transformation";
            }
            if (comps.Count(c => c.Type.Contains("Lookup", StringComparison.OrdinalIgnoreCase)) >= 2)
            {
                return "Multiple Lookup CTE joins against large reference tables";
            }
            if (task.Type.Contains("Execute Package", StringComparison.OrdinalIgnoreCase))
            {
                return "Child Package Orchestration barrier";
            }

            return "High Data Flow component throughput processing";
        }
    }
}
