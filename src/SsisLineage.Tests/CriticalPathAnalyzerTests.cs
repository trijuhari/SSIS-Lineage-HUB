using System.Linq;
using SsisLineage.Core;
using SsisLineage.Core.Models;
using Xunit;

namespace SsisLineage.Tests
{
    public class CriticalPathAnalyzerTests
    {
        [Fact]
        public void AnalyzeProject_IdentifiesLongestPathAndSlaRisk()
        {
            var graph = new LineageGraph();

            var pkg0 = new PackageNode { Id = "pkg-0", Name = "Master_Orchestration" };
            var pkg1 = new PackageNode { Id = "pkg-1", Name = "Stage_Customers" };
            var pkg2 = new PackageNode { Id = "pkg-2", Name = "Transform_Orders_Sort" };

            graph.Packages.Add(pkg0);
            graph.Packages.Add(pkg1);
            graph.Packages.Add(pkg2);

            // Fast task (Stage Customers)
            var task1 = new TaskNode
            {
                Id = "task-1",
                Name = "Stage Customers Task",
                Type = "Data Flow Task",
                PackageId = "pkg-1"
            };

            // Heavy task with blocking Sort component (Transform Orders)
            var task2 = new TaskNode
            {
                Id = "task-2",
                Name = "Transform Orders Task",
                Type = "Data Flow Task",
                PackageId = "pkg-2"
            };

            graph.Tasks.Add(task1);
            graph.Tasks.Add(task2);

            graph.Components.Add(new ComponentNode
            {
                Id = "comp-sort",
                Name = "Sort Orders",
                Type = "Sort",
                PackageId = "pkg-2",
                TaskId = "task-2"
            });

            // Execution flow: Master -> Task1 -> Task2
            graph.ExecutionEdges.Add(new ExecutionEdge
            {
                FromTaskId = "task-1",
                ToTaskId = "task-2",
                PrecedenceConstraintValue = "Success"
            });

            var report = CriticalPathAnalyzer.AnalyzeProject(graph);

            Assert.NotNull(report);
            Assert.NotEmpty(report.CriticalPathTaskIds);
            Assert.Contains("task-2", report.CriticalPathTaskIds);
            Assert.True(report.TotalEstimatedDurationSeconds > 50);
            Assert.True(report.RecommendedSlaThresholdSeconds > report.TotalEstimatedDurationSeconds);

            var task2Metric = report.TaskMetrics.FirstOrDefault(m => m.TaskId == "task-2");
            Assert.NotNull(task2Metric);
            Assert.True(task2Metric.IsOnCriticalPath);
            Assert.Contains("PERF-001", task2Metric.BottleneckReason);
        }
    }
}
