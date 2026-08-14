using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
#if WINDOWS
using Microsoft.SqlServer.Dts.Runtime;
using Microsoft.SqlServer.Dts.Pipeline.Wrapper;
#endif
using SsisLineage.Core.Models;

namespace SsisLineage.Core
{
    public class SsisPackageParser
    {
        private readonly string _projectDirectory;
        private readonly LineageGraph _graph;
        private readonly HashSet<string> _visitedPackages = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, object> _variableOverrides;
        private readonly Dictionary<string, string> _sqlVariableValues;
        private readonly Dictionary<string, string> _connectionManagerOverrides;
        private SsisConnectionManagerResolver? _connectionResolver;

        public SsisPackageParser(string projectDirectory, Dictionary<string, string>? variableOverrides = null,
            Dictionary<string, string>? sqlVariableValues = null, Dictionary<string, string>? connectionManagerOverrides = null)
        {
            _projectDirectory = projectDirectory;
            _graph = new LineageGraph();
            _variableOverrides = (variableOverrides ?? new Dictionary<string, string>())
                .ToDictionary(kv => kv.Key, kv => (object)kv.Value, StringComparer.OrdinalIgnoreCase);
            _sqlVariableValues = sqlVariableValues ?? new Dictionary<string, string>();
            _connectionManagerOverrides = connectionManagerOverrides ?? new Dictionary<string, string>();
        }

        // Resolves an Execute SQL task's connection manager reference to the actual
        // (server, database) from the project .conmgr files — placeholders like the raw
        // connection id would otherwise leak into lineage records as server/db names.
        private (string Server, string Database) ResolveConnectionServerDb(string connectionManagerRef)
        {
            if (string.IsNullOrWhiteSpace(connectionManagerRef)) return ("", "");
            try
            {
                _connectionResolver ??= new SsisConnectionManagerResolver(_projectDirectory, _connectionManagerOverrides);
                var conn = _connectionResolver.TryResolveConnectionString(connectionManagerRef);
                if (string.IsNullOrWhiteSpace(conn)) return ("", "");
                return SqlProcedureDefinitionLoader.ExtractServerAndDatabase(conn);
            }
            catch
            {
                return ("", "");
            }
        }

        // Project params fill gaps, then explicit overrides (e.g. extracted from an SSIS
        // catalog environment) win over everything.
        private void ApplyVariableLayers(Dictionary<string, object> variables)
        {
            foreach (var kv in ExpressionEvaluator.LoadProjectParameters(_projectDirectory))
            {
                variables.TryAdd(kv.Key, kv.Value);
            }
            foreach (var kv in _variableOverrides)
            {
                variables[kv.Key] = kv.Value;
            }
        }

        public LineageGraph Parse(string rootPackagePath)
        {
            _visitedPackages.Clear();
            ParsePackageRecursive(rootPackagePath, null);
            CalculateExecutionSequences();
            return _graph;
        }

        public LineageGraph ParseMultiple(IEnumerable<string> packagePaths)
        {
            _visitedPackages.Clear();
            foreach (var path in packagePaths)
            {
                ParsePackageRecursive(path, null);
            }
            CalculateExecutionSequences();
            return _graph;
        }

        private void ParsePackageRecursive(string packagePath, string? parentPackageId)
        {
            if (!File.Exists(packagePath))
            {
                Console.WriteLine($"[Warning] Package file not found: {packagePath}");
                return;
            }

            var fullPath = Path.GetFullPath(packagePath);
            if (_visitedPackages.Contains(fullPath))
            {
                _graph.Warnings.Add($"Package cycle or duplicate visit skipped: {Path.GetFileName(packagePath)}");
                return;
            }

            _visitedPackages.Add(fullPath);

            Console.WriteLine($"[*] Parsing package: {Path.GetFileName(packagePath)}");

#if WINDOWS
            try
            {
                var app = new Application();
                var package = app.LoadPackage(packagePath, null);

                var packageId = package.ID;
                var packageNode = new PackageNode
                {
                    Id = packageId,
                    Name = package.Name,
                    Path = packagePath,
                    ProjectPath = _projectDirectory,
                    FileHash = ProjectLoader.ComputeFileHash(packagePath)
                };

                // Extract connection managers (names only — kept on the package node, not graph components)
                foreach (ConnectionManager cm in package.Connections)
                {
                    packageNode.ConnectionManagers.Add(cm.Name);
                }

                // Extract variables + project parameters (Project.params) + explicit overrides
                var variables = ExpressionEvaluator.ExtractVariables(package);
                ApplyVariableLayers(variables);
                foreach (var varKey in variables.Keys)
                {
                    packageNode.Variables.Add(varKey);
                }

                _graph.Packages.Add(packageNode);

                // Recursively walk Control Flow Executables
                ProcessExecutables(package.Executables, packageNode, variables);

                // Process Precedence Constraints (Execution Edges)
                ProcessPrecedenceConstraints(package.PrecedenceConstraints);

                // Walk event handlers (OnError, OnPostExecute, …) — they can contain
                // Execute SQL / Data Flow tasks that move data just like the main flow.
                foreach (DtsEventHandler eventHandler in package.EventHandlers)
                {
                    ProcessExecutables(eventHandler.Executables, packageNode, variables);
                    ProcessPrecedenceConstraints(eventHandler.PrecedenceConstraints);
                }
            }
            catch (Exception)
            {
                // The SSIS runtime DLLs (ManagedDTS) are .NET Framework assemblies and reference
                // types (e.g. Microsoft.SqlServer.Server.SqlContext) that were removed from
                // System.Data in .NET 5+. Native loading therefore always fails on .NET 10.
                // The XML parser below produces equivalent results — this is expected behaviour.
                Console.WriteLine($"[Info] Using XML-based SSIS parser for {Path.GetFileName(packagePath)} (DTS runtime not compatible with .NET 10 — this is normal). Results are equivalent.");
                ParsePackageXmlFallback(packagePath, parentPackageId);
            }
#else
            // Cross-platform build: the SSIS DTS runtime is Windows-only and unavailable, so the
            // XML parser is the sole path (it is what the Windows build falls back to anyway).
            ParsePackageXmlFallback(packagePath, parentPackageId);
#endif
        }

#if WINDOWS
        private void ProcessExecutables(Executables executables, PackageNode packageNode, Dictionary<string, object> variables)
        {
            foreach (Executable executable in executables)
            {
                if (executable is TaskHost taskHost)
                {
                    var taskNode = new TaskNode
                    {
                        Id = taskHost.ID,
                        Name = taskHost.Name,
                        Type = taskHost.CreationName,
                        PackageId = packageNode.Id,
                        PackageName = packageNode.Name,
                        Description = taskHost.Description
                    };
                    _graph.Tasks.Add(taskNode);

                    // Execute SQL Task
                    if (taskHost.CreationName.Contains("ExecuteSQLTask"))
                    {
                        ProcessExecuteSqlTask(taskHost, packageNode, taskNode, variables);
                    }
                    // Execute Package Task (Child Package)
                    else if (taskHost.CreationName.Contains("ExecutePackageTask"))
                    {
                        ProcessExecutePackageTask(taskHost, packageNode, variables);
                    }
                    // Data Flow Task
                    else if (taskHost.CreationName.Contains("PipelineTask"))
                    {
                        ProcessDataFlowTask(taskHost, packageNode, taskNode, variables);
                    }
                    // Script Task — compiled C#/VB code is opaque to static lineage analysis
                    else if (taskHost.CreationName.Contains("ScriptTask", StringComparison.OrdinalIgnoreCase))
                    {
                        _graph.Warnings.Add(
                            $"Script Task '{taskHost.Name}' in package '{packageNode.Name}' — script code cannot be statically analysed; any data movement inside it is not captured in lineage.");
                    }
                }
                // If it's a sequence/container, drill down and collect its own precedence constraints
                else if (executable is IDTSSequence sequence)
                {
                    if (executable is Sequence seqContainer)
                        ProcessPrecedenceConstraints(seqContainer.PrecedenceConstraints);
                    else if (executable is ForEachLoop feContainer)
                        ProcessPrecedenceConstraints(feContainer.PrecedenceConstraints);
                    else if (executable is ForLoop flContainer)
                        ProcessPrecedenceConstraints(flContainer.PrecedenceConstraints);
                    ProcessExecutables(sequence.Executables, packageNode, variables);
                }
            }
        }

        private void ProcessExecuteSqlTask(TaskHost taskHost, PackageNode packageNode, TaskNode taskNode, Dictionary<string, object> variables)
        {
            try
            {
                var connection = taskHost.Properties["Connection"]?.GetValue(taskHost)?.ToString() ?? "";
                var sqlSource = taskHost.Properties["SqlStatementSource"]?.GetValue(taskHost)?.ToString() ?? "";

                // Evaluate expressions on sql source if applicable
                var sqlExpr = taskHost.Properties["SqlStatementSource"]?.GetExpression(taskHost) ?? "";
                if (!string.IsNullOrEmpty(sqlExpr))
                {
                    sqlSource = ExpressionEvaluator.Evaluate(sqlExpr, variables);
                }

                _graph.Components.Add(new ComponentNode
                {
                    Id = taskHost.ID + "_sql",
                    Name = taskHost.Name,
                    Type = "Execute SQL Task",
                    PackageId = packageNode.Id,
                    TaskId = taskHost.ID,
                    ConnectionManager = connection,
                    SqlQueryOrTable = sqlSource
                });

                if (!string.IsNullOrEmpty(sqlSource))
                {
                    if (SqlProcedureDefinitionLoader.TryParseProcedureReference(sqlSource, out _, out _))
                    {
                        _graph.Warnings.Add(
                            $"Execute SQL task '{taskHost.Name}' calls {sqlSource.Trim()} — procedure body will be loaded from SQL Server when a connection is available.");
                    }
                    else
                    {
                        var (connServer, connDb) = ResolveConnectionServerDb(connection);
                        var sqlRecords = SqlProcedureParser.Parse(sqlSource, connDb, connServer, _sqlVariableValues);
                        foreach (var rec in sqlRecords)
                        {
                            _graph.ColumnMappings.Add(BuildSqlTaskColumnMap(
                                rec, packageNode.Id, taskHost.ID, taskHost.ID + "_sql", taskHost.Name));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Failed to parse SQL Statement in {taskHost.Name}: {ex.Message}");
            }
        }

        private void ProcessExecutePackageTask(TaskHost taskHost, PackageNode packageNode, Dictionary<string, object> variables)
        {
            try
            {
                var childPackageName = taskHost.Properties["PackageName"]?.GetValue(taskHost)?.ToString() ?? "";
                if (string.IsNullOrEmpty(childPackageName))
                {
                    // Attempt to resolve via Expression
                    var expr = taskHost.Properties["PackageName"]?.GetExpression(taskHost) ?? "";
                    if (!string.IsNullOrEmpty(expr))
                    {
                        childPackageName = ExpressionEvaluator.Evaluate(expr, variables);
                    }
                }

                if (!string.IsNullOrEmpty(childPackageName))
                {
                    if (!childPackageName.EndsWith(".dtsx", StringComparison.OrdinalIgnoreCase))
                        childPackageName += ".dtsx";

                    var childPath = Path.Combine(_projectDirectory, childPackageName);
                    var packagesBefore = _graph.Packages.Count;
                    ParsePackageRecursive(childPath, packageNode.Id);

                    // Add a cross-package invokes edge so the layout pushes child package content below this task
                    if (_graph.Packages.Count > packagesBefore)
                    {
                        _graph.ExecutionEdges.Add(new ExecutionEdge
                        {
                            FromTaskId = taskHost.ID,
                            ToTaskId   = _graph.Packages[packagesBefore].Id,
                            PrecedenceConstraintValue = "Invokes"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Failed to parse Execute Package Task in {taskHost.Name}: {ex.Message}");
            }
        }

        private void ProcessDataFlowTask(TaskHost taskHost, PackageNode packageNode, TaskNode taskNode, Dictionary<string, object> variables)
        {
            try
            {
                var pipe = taskHost.InnerObject as MainPipe;
                if (pipe == null)
                {
                    // Fall back to XML representation if casting fails
                    throw new InvalidCastException("Cannot cast Data Flow Task to MainPipe");
                }

                // Component mappings
                var components = pipe.ComponentMetaDataCollection;
                var componentIdMap = new Dictionary<int, string>();

                foreach (IDTSComponentMetaData100 comp in components)
                {
                    try
                    {
                        var rawType = comp.ComponentClassID;
                        var compId = $"{taskHost.ID}_{comp.ID}";
                        var compNode = new ComponentNode
                        {
                            Id = compId,
                            Name = comp.Name,
                            Type = ThirdPartyComponentDetector.NormalizeComponentType(rawType, comp.Name),
                            PackageId = packageNode.Id,
                            TaskId = taskHost.ID
                        };

                        if (ThirdPartyComponentDetector.IsScriptComponent(rawType, comp.Name))
                        {
                            _graph.Warnings.Add(
                                $"Script Component '{comp.Name}' in package '{packageNode.Name}' — only its declared input/output columns are captured; transformation logic inside the script is opaque.");
                        }
                        else if (ThirdPartyComponentDetector.IsLikelyThirdParty(rawType, comp.Name))
                        {
                            _graph.Warnings.Add(
                                $"Third-party or custom component '{comp.Name}' in package '{packageNode.Name}' — lineage metadata may be incomplete.");
                        }

                        if (comp.RuntimeConnectionCollection.Count > 0)
                        {
                            compNode.ConnectionManager = comp.RuntimeConnectionCollection[0].ConnectionManagerID;
                        }

                        // ADO NET source/destination store the table in TableOrViewName;
                        // it only applies when no SqlCommand/OpenRowset is present.
                        var tableOrViewName = "";
                        foreach (IDTSCustomProperty100 prop in comp.CustomPropertyCollection)
                        {
                            var propValue = prop.Value?.ToString() ?? "";
                            if (propName(prop, "SqlCommand") && !string.IsNullOrEmpty(propValue))
                            {
                                compNode.SqlQueryOrTable = ExpressionEvaluator.Evaluate(propValue, variables);
                            }
                            else if (propName(prop, "OpenRowset") && !string.IsNullOrEmpty(propValue))
                            {
                                compNode.SqlQueryOrTable = ExpressionEvaluator.Evaluate(propValue, variables);
                            }
                            else if (propName(prop, "TableOrViewName") && !string.IsNullOrEmpty(propValue))
                            {
                                tableOrViewName = ExpressionEvaluator.Evaluate(propValue, variables);
                            }
                        }
                        if (string.IsNullOrEmpty(compNode.SqlQueryOrTable) && !string.IsNullOrEmpty(tableOrViewName))
                        {
                            compNode.SqlQueryOrTable = tableOrViewName;
                        }

                        _graph.Components.Add(compNode);
                        componentIdMap[comp.ID] = compId;
                    }
                    catch (Exception compEx)
                    {
                        _graph.Warnings.Add(
                            $"Failed to parse component '{comp.Name}' in data flow '{taskHost.Name}': {compEx.Message}");
                    }
                }

                // Paths connecting components
                foreach (IDTSPath100 path in pipe.PathCollection)
                {
                    _graph.DataFlowEdges.Add(new DataFlowEdge
                    {
                        FromComponentId = $"{taskHost.ID}_{path.StartPoint.Component.ID}",
                        ToComponentId = $"{taskHost.ID}_{path.EndPoint.Component.ID}",
                        PathRefId = path.ID.ToString()
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Native Data Flow processing failed for {taskHost.Name}: {ex.Message}. Falling back to XML-based parsing for this Data Flow.");
                ParseDataFlowXmlFallback(packageNode, taskNode, taskHost.ID);
            }

            bool propName(IDTSCustomProperty100 p, string n) => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase);
        }

#endif
        // Builds a ColumnMap from a parsed SQL lineage record, carrying ALL extracted fields
        // (source/target server·db·schema·table, expression, join + filter conditions).
        // Shared by the native and XML-fallback Execute SQL handlers so neither path drops data.
        private static ColumnMap BuildSqlTaskColumnMap(
            SqlLineageRecord rec, string packageId, string taskId, string componentId, string taskName)
        {
            return new ColumnMap
            {
                PackageId = packageId,
                TaskId = taskId,
                SourceComponentId = componentId,
                SourceComponentName = string.IsNullOrWhiteSpace(rec.SourceTable)
                    ? taskName
                    : (string.IsNullOrWhiteSpace(rec.SourceSchema) ? rec.SourceTable : $"{rec.SourceSchema}.{rec.SourceTable}"),
                SourceServer     = rec.SourceServer,
                SourceDatabase   = rec.SourceDatabase,
                SourceSchema     = rec.SourceSchema,
                SourceTable      = rec.SourceTable,
                SourceColumnName = rec.SourceColumnName,
                SourceExpression = rec.SourceExpression,
                TargetComponentId = componentId,
                TargetComponentName = string.IsNullOrWhiteSpace(rec.TargetTable)
                    ? taskName
                    : (string.IsNullOrWhiteSpace(rec.TargetSchema) ? rec.TargetTable : $"{rec.TargetSchema}.{rec.TargetTable}"),
                TargetServer     = rec.TargetServer,
                TargetDatabase   = rec.TargetDatabase,
                TargetSchema     = rec.TargetSchema,
                TargetTable      = rec.TargetTable,
                TargetColumnName = rec.TargetColumnName,
                OperationType    = rec.OperationType,
                JoinDetails      = rec.JoinDetails,
                FilterConditions = rec.FilterConditions
            };
        }

#if WINDOWS
        private void ProcessPrecedenceConstraints(PrecedenceConstraints constraints)
        {
            foreach (PrecedenceConstraint pc in constraints)
            {
                var from = (pc.PrecedenceExecutable as TaskHost)?.ID ?? "";
                var to = (pc.ConstrainedExecutable as TaskHost)?.ID ?? "";
                if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) continue;
                _graph.ExecutionEdges.Add(new ExecutionEdge
                {
                    FromTaskId = from,
                    ToTaskId = to,
                    PrecedenceConstraintValue = pc.Value.ToString(),
                    Expression = pc.Expression
                });
            }
        }
#endif

        #region XML Fallback Parsers
        private string GetExecutableId(XElement exe, XNamespace dts)
        {
            var dtsId = exe.Attribute(dts + "DTSID")?.Value 
                ?? exe.Elements(dts + "Property").FirstOrDefault(p => p.Attribute(dts + "Name")?.Value == "DTSID")?.Value;
            if (!string.IsNullOrEmpty(dtsId)) return dtsId;

            var refId = exe.Attribute(dts + "refId")?.Value 
                ?? exe.Elements(dts + "Property").FirstOrDefault(p => p.Attribute(dts + "Name")?.Value == "refId")?.Value;
            return refId ?? "";
        }

        private string GetExecutableName(XElement exe, XNamespace dts)
        {
            var name = exe.Attribute(dts + "ObjectName")?.Value 
                ?? exe.Elements(dts + "Property").FirstOrDefault(p => p.Attribute(dts + "Name")?.Value == "ObjectName")?.Value;
            return name ?? "";
        }

        private string GetExecutableType(XElement exe, XNamespace dts)
        {
            var type = exe.Attribute(dts + "ExecutableType")?.Value 
                ?? exe.Attribute(dts + "CreationName")?.Value 
                ?? exe.Elements(dts + "Property").FirstOrDefault(p => p.Attribute(dts + "Name")?.Value == "CreationName")?.Value 
                ?? exe.Elements(dts + "Property").FirstOrDefault(p => p.Attribute(dts + "Name")?.Value == "ExecutableType")?.Value;
            return type ?? "";
        }

        private string ResolveComponentIdFromEndpoint(XElement exeNode, string endpointId)
        {
            if (string.IsNullOrEmpty(endpointId)) return "";
            var comp = exeNode.Descendants()
                .FirstOrDefault(x => (x.Name.LocalName == "output" || x.Name.LocalName == "input") 
                                     && (x.Attribute("id")?.Value == endpointId || x.Attribute("refId")?.Value == endpointId));
            if (comp != null)
            {
                var parentComp = comp.Ancestors().FirstOrDefault(x => x.Name.LocalName == "component");
                if (parentComp != null)
                {
                    return parentComp.Attribute("refId")?.Value ?? parentComp.Attribute("id")?.Value ?? "";
                }
            }
            return StripPathEndpointSuffix(endpointId);
        }

        private string ResolveComponentIdFromLineageId(XElement exeNode, string lineageId)
        {
            if (string.IsNullOrEmpty(lineageId)) return "";
            var col = exeNode.Descendants()
                .FirstOrDefault(x => (x.Name.LocalName == "outputColumn" || x.Name.LocalName == "inputColumn" || x.Name.LocalName == "externalMetadataColumn")
                                     && (x.Attribute("lineageId")?.Value == lineageId || x.Attribute("id")?.Value == lineageId));
            if (col != null)
            {
                var parentComp = col.Ancestors().FirstOrDefault(x => x.Name.LocalName == "component");
                if (parentComp != null)
                {
                    return parentComp.Attribute("refId")?.Value ?? parentComp.Attribute("id")?.Value ?? "";
                }
            }
            var extracted = ExtractComponentIdFromLineageId(lineageId);
            return !string.IsNullOrEmpty(extracted) ? extracted : lineageId;
        }

        private string ResolveComponentIdFromColumnName(XElement exeNode, string colName)
        {
            if (string.IsNullOrEmpty(colName)) return "";
            var col = exeNode.Descendants()
                .FirstOrDefault(x => x.Name.LocalName == "outputColumn" &&
                    (string.Equals(x.Attribute("name")?.Value, colName, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(x.Attribute("cachedName")?.Value, colName, StringComparison.OrdinalIgnoreCase)));
            if (col != null)
            {
                var parentComp = col.Ancestors().FirstOrDefault(x => x.Name.LocalName == "component");
                if (parentComp != null)
                {
                    return parentComp.Attribute("refId")?.Value ?? parentComp.Attribute("id")?.Value ?? "";
                }
            }
            return "";
        }

        private string ResolveColumnNameFromLineageId(XElement exeNode, string lineageId, string fallbackName)
        {
            if (string.IsNullOrEmpty(lineageId)) return fallbackName;
            var col = exeNode.Descendants()
                .FirstOrDefault(x => (x.Name.LocalName == "outputColumn" || x.Name.LocalName == "inputColumn")
                                     && (x.Attribute("lineageId")?.Value == lineageId || x.Attribute("id")?.Value == lineageId));
            if (col != null)
            {
                var name = col.Attribute("name")?.Value ?? col.Attribute("cachedName")?.Value;
                if (!string.IsNullOrEmpty(name)) return name;
            }
            return fallbackName;
        }

        private string ResolveComponentName(string compId, XElement exeNode)
        {
            if (string.IsNullOrWhiteSpace(compId)) return "";

            var compNode = _graph.Components.FirstOrDefault(c => c.Id == compId);
            if (compNode != null && !string.IsNullOrWhiteSpace(compNode.Name)) return compNode.Name;

            var rawId = compId.Contains("::") ? compId.Split("::").Last() : compId;
            var comp = exeNode.Descendants()
                .FirstOrDefault(x => x.Name.LocalName == "component" &&
                    (x.Attribute("refId")?.Value == rawId || x.Attribute("id")?.Value == rawId ||
                     x.Attribute("refId")?.Value == compId || x.Attribute("id")?.Value == compId));
            if (comp != null)
            {
                var name = comp.Attribute("name")?.Value;
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }

            var extracted = ExtractComponentNameFromRefId(rawId);
            if (!string.IsNullOrWhiteSpace(extracted)) return extracted;

            return !string.IsNullOrWhiteSpace(rawId) && rawId != "?" ? rawId : "";
        }

        private string GetTargetColumnName(XElement inCol)
        {
            var refId = inCol.Attribute("refId")?.Value;
            var targetCol = ExtractColumnNameFromRefId(refId);
            if (!string.IsNullOrEmpty(targetCol)) return targetCol;

            var name = inCol.Attribute("name")?.Value;
            if (!string.IsNullOrEmpty(name)) return name;

            var extId = inCol.Attribute("externalMetadataColumnId")?.Value;
            if (!string.IsNullOrEmpty(extId))
            {
                var comp = inCol.Ancestors().FirstOrDefault(x => x.Name.LocalName == "component");
                if (comp != null)
                {
                    var extCol = comp.Descendants()
                        .FirstOrDefault(x => x.Name.LocalName == "externalMetadataColumn" && x.Attribute("id")?.Value == extId);
                    if (extCol != null)
                    {
                        var extName = extCol.Attribute("name")?.Value;
                        if (!string.IsNullOrEmpty(extName)) return extName;
                    }
                }
            }

            var cachedName = inCol.Attribute("cachedName")?.Value;
            if (!string.IsNullOrEmpty(cachedName)) return cachedName;

            return "";
        }

        private void ParsePackageXmlFallback(string packagePath, string? parentPackageId)
        {
            try
            {
                var doc = XDocument.Load(packagePath);
                var root = doc.Root;
                if (root == null) return;

                XNamespace dts = "www.microsoft.com/SqlServer/Dts";

                var packageId = root.Attribute(dts + "DTSID")?.Value
                    ?? root.Attribute(dts + "refId")?.Value
                    ?? Path.GetFileNameWithoutExtension(packagePath);
                var packageNode = new PackageNode
                {
                    Id = packageId,
                    Name = root.Attribute(dts + "ObjectName")?.Value ?? Path.GetFileNameWithoutExtension(packagePath),
                    Path = packagePath,
                    ProjectPath = _projectDirectory,
                    FileHash = ProjectLoader.ComputeFileHash(packagePath)
                };

                var variables = ExpressionEvaluator.ExtractVariablesFromXml(doc);
                ApplyVariableLayers(variables);
                foreach (var varKey in variables.Keys)
                {
                    packageNode.Variables.Add(varKey);
                }

                foreach (var cm in doc.Descendants(dts + "ConnectionManager"))
                {
                    var objName = cm.Attribute(dts + "ObjectName")?.Value ?? cm.Attribute("ObjectName")?.Value;
                    var dtsId = cm.Attribute(dts + "DTSID")?.Value ?? cm.Attribute("DTSID")?.Value;
                    var refId = cm.Attribute(dts + "refId")?.Value ?? cm.Attribute("refId")?.Value;
                    var connStr = SsisConnectionManagerResolver.FindConnectionString(cm);
                    if (!string.IsNullOrWhiteSpace(connStr))
                    {
                        _connectionResolver ??= new SsisConnectionManagerResolver(_projectDirectory, _connectionManagerOverrides);
                        if (!string.IsNullOrWhiteSpace(objName)) _connectionResolver.AddConnection(objName, connStr);
                        if (!string.IsNullOrWhiteSpace(dtsId)) _connectionResolver.AddConnection(dtsId, connStr);
                        if (!string.IsNullOrWhiteSpace(refId)) _connectionResolver.AddConnection(refId, connStr);
                    }
                    if (!string.IsNullOrWhiteSpace(objName) && !packageNode.ConnectionManagers.Contains(objName))
                    {
                        packageNode.ConnectionManagers.Add(objName);
                    }
                }

                _graph.Packages.Add(packageNode);

                var executables = doc.Descendants(dts + "Executable")
                    .Where(x => x != root && !x.Elements(dts + "Executables").Any());
                foreach (var exe in executables)
                {
                    var rawExeId = GetExecutableId(exe, dts);
                    var exeName = GetExecutableName(exe, dts);
                    var exeType = GetExecutableType(exe, dts);

                    if (string.IsNullOrEmpty(rawExeId)) continue;
                    var exeId = QualifyId(packageId, rawExeId);

                    var taskNode = new TaskNode
                    {
                        Id = exeId,
                        Name = exeName,
                        Type = exeType,
                        PackageId = packageId,
                        PackageName = packageNode.Name
                    };
                    _graph.Tasks.Add(taskNode);

                    if (exeType.Contains("ExecutePackageTask"))
                    {
                        var childPkgNameNode = exe.Descendants("PackageName").FirstOrDefault();
                        if (childPkgNameNode != null && !string.IsNullOrEmpty(childPkgNameNode.Value))
                        {
                            var childName = childPkgNameNode.Value;
                            taskNode.Description = childName;
                            if (!childName.EndsWith(".dtsx", StringComparison.OrdinalIgnoreCase))
                                childName += ".dtsx";

                            var childPath = Path.Combine(_projectDirectory, childName);
                            var packagesBefore = _graph.Packages.Count;
                            ParsePackageRecursive(childPath, packageId);

                            if (_graph.Packages.Count > packagesBefore)
                            {
                                _graph.ExecutionEdges.Add(new ExecutionEdge
                                {
                                    FromTaskId = exeId,
                                    ToTaskId   = _graph.Packages[packagesBefore].Id,
                                    PrecedenceConstraintValue = "Invokes"
                                });
                            }
                        }
                    }
                    else if (IsDataFlowTask(exeType))
                    {
                        ParseDataFlowXmlFallback(packageNode, taskNode, rawExeId);
                    }
                    else if (exeType.Contains("ExecuteSQLTask", StringComparison.OrdinalIgnoreCase))
                    {
                        ParseExecuteSqlTaskXmlFallback(exe, packageNode, taskNode, variables);
                    }
                    else if (exeType.Contains("ScriptTask", StringComparison.OrdinalIgnoreCase))
                    {
                        _graph.Warnings.Add(
                            $"Script Task '{exeName}' in package '{packageNode.Name}' — script code cannot be statically analysed; any data movement inside it is not captured in lineage.");
                    }
                }

                foreach (var constraint in doc.Descendants(dts + "PrecedenceConstraint"))
                {
                    var from = constraint.Attribute(dts + "From")?.Value ?? "";
                    var to = constraint.Attribute(dts + "To")?.Value ?? "";
                    if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                    {
                        continue;
                    }

                    _graph.ExecutionEdges.Add(new ExecutionEdge
                    {
                        FromTaskId = QualifyId(packageId, from),
                        ToTaskId = QualifyId(packageId, to),
                        PrecedenceConstraintValue = constraint.Attribute(dts + "Value")?.Value ?? "Success",
                        Expression = constraint.Attribute(dts + "Expression")?.Value ?? ""
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] XML fallback parsing failed for package {Path.GetFileName(packagePath)}: {ex.Message}");
            }
        }

        private void ParseExecuteSqlTaskXmlFallback(XElement exe, PackageNode packageNode, TaskNode taskNode,
            Dictionary<string, object> variables)
        {
            try
            {
                XNamespace sqlTask = "www.microsoft.com/sqlserver/dts/tasks/sqltask";
                var sqlData = exe.Descendants()
                    .FirstOrDefault(x => x.Name.LocalName.Equals("SqlTaskData", StringComparison.OrdinalIgnoreCase));

                if (sqlData == null)
                {
                    return;
                }

                var sqlSource = sqlData.Attribute(sqlTask + "SqlStatementSource")?.Value
                    ?? sqlData.Attribute("SqlStatementSource")?.Value
                    ?? "";
                var connection = sqlData.Attribute(sqlTask + "Connection")?.Value
                    ?? sqlData.Attribute("Connection")?.Value
                    ?? "";

                if (ExpressionEvaluator.IsSingleVariableReference(sqlSource))
                {
                    var resolved = ExpressionEvaluator.Evaluate(sqlSource, variables);
                    if (!string.IsNullOrWhiteSpace(resolved)) sqlSource = resolved;
                }

                var bindings = new List<string>();
                foreach (var pb in sqlData.Descendants().Where(x => x.Name.LocalName == "ParameterBinding"))
                {
                    var paramName = pb.Attribute(sqlTask + "ParameterName")?.Value ?? pb.Attribute("ParameterName")?.Value ?? "?";
                    var varName = pb.Attribute(sqlTask + "DtsVariableName")?.Value ?? pb.Attribute("DtsVariableName")?.Value ?? "";
                    var direction = pb.Attribute(sqlTask + "ParameterDirection")?.Value ?? pb.Attribute("ParameterDirection")?.Value ?? "Input";
                    if (!string.IsNullOrEmpty(varName))
                        bindings.Add($"@{paramName} ← {varName} ({direction})");
                }
                foreach (var rb in sqlData.Descendants().Where(x => x.Name.LocalName == "ResultBinding"))
                {
                    var resultName = rb.Attribute(sqlTask + "ResultName")?.Value ?? rb.Attribute("ResultName")?.Value ?? "0";
                    var varName = rb.Attribute(sqlTask + "DtsVariableName")?.Value ?? rb.Attribute("DtsVariableName")?.Value ?? "";
                    if (!string.IsNullOrEmpty(varName))
                        bindings.Add($"Result[{resultName}] → {varName}");
                }

                var componentId = taskNode.Id + "_sql";
                _graph.Components.Add(new ComponentNode
                {
                    Id = componentId,
                    Name = taskNode.Name,
                    Type = "Execute SQL Task",
                    PackageId = packageNode.Id,
                    TaskId = taskNode.Id,
                    ConnectionManager = connection,
                    SqlQueryOrTable = sqlSource,
                    ParameterBindings = bindings
                });

                if (string.IsNullOrWhiteSpace(sqlSource)) return;

                // Stored-proc reference → body is loaded from SQL Server by the enricher.
                if (SqlProcedureDefinitionLoader.TryParseProcedureReference(sqlSource, out _, out _))
                {
                    _graph.Warnings.Add(
                        $"Execute SQL task '{taskNode.Name}' calls {sqlSource.Trim()} — procedure body will be loaded from SQL Server when a connection is available.");
                    return;
                }

                // Inline SQL — parse for column lineage (INSERT/UPDATE/DELETE/MERGE/SELECT INTO,
                // CTEs, dynamic SQL), same as the native path. OLE DB positional parameters (?)
                // are substituted so ScriptDom can parse the statement.
                var parsableSql = SqlProcedureParser.ReplacePositionalParameters(sqlSource);
                var (connServer, connDb) = ResolveConnectionServerDb(connection);
                var sqlRecords = SqlProcedureParser.Parse(parsableSql, connDb, connServer, _sqlVariableValues);
                foreach (var rec in sqlRecords)
                {
                    _graph.ColumnMappings.Add(BuildSqlTaskColumnMap(
                        rec, packageNode.Id, taskNode.Id, componentId, taskNode.Name));
                }
            }
            catch (Exception ex)
            {
                _graph.Warnings.Add($"Failed to parse Execute SQL task '{taskNode.Name}' from XML: {ex.Message}");
            }
        }

        private void ParseDataFlowXmlFallback(PackageNode packageNode, TaskNode taskNode, string taskRefId)
        {
            try
            {
                var doc = XDocument.Load(packageNode.Path);
                XNamespace dts = "www.microsoft.com/SqlServer/Dts";

                // Locate the executable node with taskRefId (matching raw refId, qualified refId, or suffix)
                var exeNode = doc.Descendants(dts + "Executable")
                    .FirstOrDefault(x => {
                        var id = GetExecutableId(x, dts);
                        return id == taskRefId || QualifyId(packageNode.Id, id) == taskRefId ||
                               id.EndsWith(taskRefId, StringComparison.OrdinalIgnoreCase);
                    });

                if (exeNode == null) return;

                // Enumerate pipeline components
                var components = exeNode.Descendants()
                    .Where(x => x.Name.LocalName == "component");
                foreach (var comp in components)
                {
                    var rawCompId = comp.Attribute("refId")?.Value ?? comp.Attribute("id")?.Value ?? "";
                    var compId = QualifyId(taskNode.Id, rawCompId);
                    var compName = comp.Attribute("name")?.Value ?? "";
                    var compType = comp.Attribute("componentClassID")?.Value ?? "";

                    var compNode = new ComponentNode
                    {
                        Id = compId,
                        Name = compName,
                        Type = ThirdPartyComponentDetector.NormalizeComponentType(compType, compName),
                        PackageId = packageNode.Id,
                        TaskId = taskNode.Id
                    };

                    if (ThirdPartyComponentDetector.IsScriptComponent(compType, compName))
                    {
                        _graph.Warnings.Add(
                            $"Script Component '{compName}' in package '{packageNode.Name}' — only its declared input/output columns are captured; transformation logic inside the script is opaque.");
                    }
                    else if (ThirdPartyComponentDetector.IsLikelyThirdParty(compType, compName))
                    {
                        _graph.Warnings.Add(
                            $"Third-party or custom component '{compName}' (XML fallback) — lineage metadata may be incomplete.");
                    }

                    // Connection manager for this component
                    var compCm = comp.Descendants()
                        .FirstOrDefault(x => x.Name.LocalName == "connection" && (x.Attribute("connectionManagerRefId") != null || x.Attribute("connectionManagerID") != null || x.Attribute("connectionRef") != null));
                    string cmRef = "";
                    if (compCm != null)
                    {
                        cmRef = compCm.Attribute("connectionManagerRefId")?.Value 
                            ?? compCm.Attribute("connectionManagerID")?.Value 
                            ?? compCm.Attribute("connectionRef")?.Value 
                            ?? "";
                    }
                    else
                    {
                        cmRef = comp.Attribute("connectionRef")?.Value 
                            ?? comp.Attribute("connectionManagerID")?.Value 
                            ?? comp.Attribute("connectionManagerRefId")?.Value 
                            ?? "";
                    }

                    if (!string.IsNullOrEmpty(cmRef))
                    {
                        compNode.ConnectionManager = cmRef.Contains(":") ? cmRef.Split(':').Last() : cmRef;
                    }

                    // SQL statement or table name property
                    var sqlProp = comp.Descendants()
                        .FirstOrDefault(x => x.Name.LocalName == "property" && (x.Attribute("name")?.Value == "SqlCommand" || x.Attribute("name")?.Value == "OpenRowset"));
                    if (sqlProp != null && !string.IsNullOrWhiteSpace(sqlProp.Value))
                    {
                        compNode.SqlQueryOrTable = sqlProp.Value;
                    }

                    _graph.Components.Add(compNode);

                    // Extract input columns and mappings
                    var inputCols = comp.Descendants()
                        .Where(x => x.Name.LocalName == "inputColumn");
                    foreach (var inCol in inputCols)
                    {
                        var lineageId = inCol.Attribute("cachedLineageId")?.Value ?? inCol.Attribute("lineageId")?.Value ?? inCol.Attribute("id")?.Value ?? "";
                        var fallbackColName = inCol.Attribute("name")?.Value ?? inCol.Attribute("cachedName")?.Value ?? "";
                        var sourceColName = ResolveColumnNameFromLineageId(exeNode, lineageId, fallbackColName);
                        if (string.IsNullOrEmpty(sourceColName)) sourceColName = fallbackColName;

                        var sourceCompId = ResolveComponentIdFromLineageId(exeNode, lineageId);
                        if (string.IsNullOrEmpty(sourceCompId) && !string.IsNullOrEmpty(sourceColName))
                        {
                            sourceCompId = ResolveComponentIdFromColumnName(exeNode, sourceColName);
                        }
                        var sourceCompName = ResolveComponentName(sourceCompId, exeNode);
                        var targetColName = GetTargetColumnName(inCol);
                        if (string.IsNullOrEmpty(targetColName)) targetColName = sourceColName;

                        // Capture Lookup join key: joinToReferenceColumn declares which column in the
                        // reference dataset this input column is matched against. We store this as
                        // "LOOKUP_JOIN:sourceCol=refCol" in JoinDetails so the dbt converter can
                        // correctly resolve chained-lookup joins (e.g. RegionCode coming from lookup_0
                        // joins lookup_1 ON lookup_0.RegionCode = lookup_1.RegionCode).
                        var joinDetails = "";
                        var joinToRef = inCol.Attribute("joinToReferenceColumn")?.Value;
                        if (!string.IsNullOrEmpty(joinToRef))
                        {
                            var joinSrcCol = !string.IsNullOrEmpty(sourceColName) ? sourceColName : fallbackColName;
                            joinDetails = $"LOOKUP_JOIN:{joinSrcCol}={joinToRef}";
                        }

                        _graph.ColumnMappings.Add(new ColumnMap
                        {
                            PackageId = packageNode.Id,
                            TaskId = taskNode.Id,
                            SourceComponentId = sourceCompId,
                            SourceComponentName = sourceCompName,
                            SourceColumnName = sourceColName,
                            TargetComponentId = compId,
                            TargetComponentName = compName,
                            TargetColumnName = targetColName,
                            OperationType = compNode.Type ?? "DataFlow",
                            JoinDetails = joinDetails
                        });
                    }

                    // These are NOT captured by the inputColumn loop above (which only sees downstream
                    // consumers), so we add them here as ColumnMaps with SourceExpression filled in.
                    var normalizedType = compNode.Type ?? "";
                    if (normalizedType.Contains("Derived Column", StringComparison.OrdinalIgnoreCase) ||
                        compType.Contains("DerivedColumn", StringComparison.OrdinalIgnoreCase) ||
                        compName.Contains("Derived Column", StringComparison.OrdinalIgnoreCase))
                    {
                        var derivedOutputCols = comp.Descendants()
                            .Where(x => x.Name.LocalName == "outputColumn");
                        foreach (var outCol in derivedOutputCols)
                        {
                            var outColName = outCol.Attribute("name")?.Value ?? outCol.Attribute("cachedName")?.Value ?? "";
                            var ssisExpr = outCol.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("expression", StringComparison.OrdinalIgnoreCase))?.Value
                                           ?? outCol.Descendants().FirstOrDefault(p => p.Attribute("name")?.Value?.Equals("expression", StringComparison.OrdinalIgnoreCase) == true)?.Value
                                           ?? "";
                            // Decode XML-encoded operators (e.g. &gt; → >, &amp; → &)
                            ssisExpr = System.Net.WebUtility.HtmlDecode(ssisExpr);

                            if (string.IsNullOrEmpty(outColName) || string.IsNullOrEmpty(ssisExpr)) continue;

                            _graph.ColumnMappings.Add(new ColumnMap
                            {
                                PackageId           = packageNode.Id,
                                TaskId              = taskNode.Id,
                                SourceComponentId   = compId,
                                SourceComponentName = compName, // "Derived Column"
                                SourceColumnName    = outColName,
                                SourceExpression    = ssisExpr,
                                TargetComponentId   = compId,
                                TargetComponentName = compName,
                                TargetColumnName    = outColName,
                                OperationType       = "DERIVED_COLUMN"
                            });
                        }
                    }
                }

                // Enumerate pipeline paths — strip .Outputs[...] / .Inputs[...] suffixes so IDs match component refIds
                var paths = exeNode.Descendants()
                    .Where(x => x.Name.LocalName == "path");
                foreach (var path in paths)
                {
                    var pathId  = path.Attribute("refId")?.Value ?? path.Attribute("id")?.Value ?? "";
                    var rawStartId = ResolveComponentIdFromEndpoint(exeNode, path.Attribute("startId")?.Value ?? "");
                    var rawEndId   = ResolveComponentIdFromEndpoint(exeNode, path.Attribute("endId")?.Value ?? "");
                    var startId = QualifyId(taskNode.Id, rawStartId);
                    var endId   = QualifyId(taskNode.Id, rawEndId);

                    _graph.DataFlowEdges.Add(new DataFlowEdge
                    {
                        FromComponentId = startId,
                        ToComponentId   = endId,
                        PathRefId       = pathId
                    });
                }
            }
            catch (Exception ex)
            {
                _graph.Warnings.Add($"XML fallback parsing failed for Data Flow '{taskNode.Name}': {ex.Message}");
                Console.WriteLine($"[Warning] XML fallback parsing failed for Data Flow {taskNode.Name}: {ex.Message}");
            }
        }

        private static bool IsDataFlowTask(string executableType)
        {
            return executableType.Contains("Pipeline", StringComparison.OrdinalIgnoreCase)
                || executableType.Contains("PipelineTask", StringComparison.OrdinalIgnoreCase);
        }

        private static string? ExtractColumnNameFromRefId(string? refId)
        {
            if (string.IsNullOrWhiteSpace(refId))
            {
                return null;
            }

            var marker = ".Columns[";
            var start = refId.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return null;
            }

            start += marker.Length;
            var end = refId.IndexOf(']', start);
            return end > start ? refId.Substring(start, end - start) : null;
        }

        // Strip .Outputs[...] or .Inputs[...] tail from a pipeline path endpoint ID to get the bare component refId
        private static string StripPathEndpointSuffix(string id)
        {
            if (string.IsNullOrEmpty(id)) return id;
            var idx = id.IndexOf(".Outputs[", StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return id.Substring(0, idx);
            idx = id.IndexOf(".Inputs[", StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return id.Substring(0, idx);
            return id;
        }

        private static string? ExtractComponentIdFromLineageId(string lineageId)
        {
            if (string.IsNullOrWhiteSpace(lineageId))
            {
                return null;
            }

            var outputsIndex = lineageId.IndexOf(".Outputs[", StringComparison.OrdinalIgnoreCase);
            return outputsIndex > 0 ? lineageId.Substring(0, outputsIndex) : lineageId;
        }

        private static string? ExtractComponentNameFromRefId(string refId)
        {
            if (string.IsNullOrWhiteSpace(refId))
            {
                return null;
            }

            var parts = refId.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[^1] : null;
        }
        private static string QualifyId(string prefix, string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return id;
            if (id.StartsWith("{") && id.EndsWith("}")) return id;
            if (id.StartsWith(prefix + "::", StringComparison.OrdinalIgnoreCase)) return id;
            return $"{prefix}::{id}";
        }

        public static (string? schema, string? table) ExtractSchemaAndTable(string? sqlOrTable)
        {
            if (string.IsNullOrWhiteSpace(sqlOrTable)) return (null, null);

            var trimmed = sqlOrTable.Trim();

            if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.Contains('\n') &&
                !trimmed.Substring(0, Math.Min(10, trimmed.Length)).Contains(' '))
            {
                var parts = trimmed.Replace("[", "").Replace("]", "").Split('.');
                if (parts.Length == 3) return (parts[1], parts[2]); // 3-part name: db.schema.table
                if (parts.Length == 2) return (parts[0], parts[1]);
                if (parts.Length == 1) return (null, parts[0]);
            }

            var match = System.Text.RegularExpressions.Regex.Match(
                trimmed, @"\bFROM\s+(?:\[?[a-zA-Z0-9_]+\]?\.)?\[?([a-zA-Z0-9_]+)\]?(?:\.\[?([a-zA-Z0-9_]+)\]?)?",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (match.Success)
            {
                if (!string.IsNullOrEmpty(match.Groups[2].Value))
                {
                    return (match.Groups[1].Value, match.Groups[2].Value);
                }
                return (null, match.Groups[1].Value);
            }

            return (null, null);
        }
        #endregion

        private void CalculateExecutionSequences()
        {
            // 1. Sort Tasks
            var taskInDegree = _graph.Tasks.ToDictionary(t => t.Id, t => 0);
            var taskAdj = _graph.Tasks.ToDictionary(t => t.Id, t => new List<string>());

            foreach (var edge in _graph.ExecutionEdges)
            {
                if (taskAdj.ContainsKey(edge.FromTaskId) && taskAdj.ContainsKey(edge.ToTaskId))
                {
                    taskAdj[edge.FromTaskId].Add(edge.ToTaskId);
                    taskInDegree[edge.ToTaskId]++;
                }
            }

            var taskQueue = new Queue<TaskNode>(_graph.Tasks.Where(t => taskInDegree[t.Id] == 0).OrderBy(t => t.Name));
            int taskSeq = 0;
            var processedTasks = new HashSet<string>();

            while (taskQueue.Count > 0)
            {
                var t = taskQueue.Dequeue();
                if (!processedTasks.Add(t.Id)) continue;
                t.ExecutionSequence = ++taskSeq;

                foreach (var neighbor in taskAdj[t.Id])
                {
                    taskInDegree[neighbor]--;
                    if (taskInDegree[neighbor] == 0)
                    {
                        var nNode = _graph.Tasks.FirstOrDefault(n => n.Id == neighbor);
                        if (nNode != null) taskQueue.Enqueue(nNode);
                    }
                }
            }
            
            // Handle disconnected/cycles for tasks
            foreach (var t in _graph.Tasks.Where(x => !processedTasks.Contains(x.Id)).OrderBy(x => x.Name))
            {
                t.ExecutionSequence = ++taskSeq;
            }

            // 2. Sort Components
            var compInDegree = _graph.Components.ToDictionary(c => c.Id, c => 0);
            var compAdj = _graph.Components.ToDictionary(c => c.Id, c => new List<string>());

            foreach (var edge in _graph.DataFlowEdges)
            {
                if (compAdj.ContainsKey(edge.FromComponentId) && compAdj.ContainsKey(edge.ToComponentId))
                {
                    compAdj[edge.FromComponentId].Add(edge.ToComponentId);
                    compInDegree[edge.ToComponentId]++;
                }
            }

            var compQueue = new Queue<ComponentNode>(_graph.Components.Where(c => compInDegree[c.Id] == 0).OrderBy(c => c.Name));
            int compSeq = 0;
            var processedComps = new HashSet<string>();

            while (compQueue.Count > 0)
            {
                var c = compQueue.Dequeue();
                if (!processedComps.Add(c.Id)) continue;
                c.ExecutionSequence = ++compSeq;

                foreach (var neighbor in compAdj[c.Id])
                {
                    compInDegree[neighbor]--;
                    if (compInDegree[neighbor] == 0)
                    {
                        var nNode = _graph.Components.FirstOrDefault(n => n.Id == neighbor);
                        if (nNode != null) compQueue.Enqueue(nNode);
                    }
                }
            }

            // Handle disconnected/cycles for components
            foreach (var c in _graph.Components.Where(x => !processedComps.Contains(x.Id)).OrderBy(x => x.Name))
            {
                c.ExecutionSequence = ++compSeq;
            }

            // 3. Reorder the lists in the graph
            _graph.Tasks = _graph.Tasks.OrderBy(t => t.ExecutionSequence).ToList();
            _graph.Components = _graph.Components.OrderBy(c => c.ExecutionSequence).ToList();

            var taskSeqDict = _graph.Tasks.ToDictionary(t => t.Id, t => t.ExecutionSequence);
            var compSeqDict = _graph.Components.ToDictionary(c => c.Id, c => c.ExecutionSequence);
            
            _graph.ColumnMappings = _graph.ColumnMappings
                .OrderBy(m => taskSeqDict.TryGetValue(m.TaskId, out var ts) ? ts : 999999)
                .ThenBy(m => compSeqDict.TryGetValue(m.SourceComponentId, out var cs) ? cs : 999999)
                .ToList();
        }
    }
}
