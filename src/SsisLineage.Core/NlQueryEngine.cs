using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SsisLineage.Core.Models;

namespace SsisLineage.Core
{
    // ── Intent model ──────────────────────────────────────────────────────────

    public enum NlQueryIntent
    {
        FindColumnSource,          // "where does X come from?"
        FindColumnTarget,          // "where does X go?"
        FindPackagesWritingToTable,// "which package writes to X?"
        FindPackagesReadingFromTable, // "which package reads from X?"
        FindTableMappings,         // "what mappings exist in table X?"
        FindHighFanoutColumns,     // "which column is used the most?"
        FindOrphanTables,          // "which tables have no downstream?"
        FindPackageForColumn,      // "which package uses column X?"
        FindAllPackages,           // "list all packages"
        Unknown
    }

    public sealed class ParsedNlQuery
    {
        public NlQueryIntent Intent { get; set; } = NlQueryIntent.Unknown;
        public string Entity { get; set; } = "";      // table or column name extracted
        public string EntityType { get; set; } = "";  // "column" | "table" | ""
        public int Threshold { get; set; } = 2;       // for fanout queries
        public string Original { get; set; } = "";
        public string IntentLabel { get; set; } = "";
    }

    // ── Result model ──────────────────────────────────────────────────────────

    public sealed class NlResultRow
    {
        public string Primary { get; set; } = "";
        public string Secondary { get; set; } = "";
        public string Category { get; set; } = "";
        public int Count { get; set; }
    }

    public sealed class NlQueryResult
    {
        public string Summary { get; set; } = "";
        public List<NlResultRow> Rows { get; set; } = new();
        public List<string> FollowUps { get; set; } = new();
        public bool HasResults => Rows.Count > 0;
        public ParsedNlQuery Query { get; set; } = new();
    }

    // ── Parser ────────────────────────────────────────────────────────────────

    public static class NlQueryParser
    {
        // Bilingual keyword patterns — order matters (most specific first)
        private static readonly (NlQueryIntent Intent, string[] Keywords, string Label)[] _rules = new[]
        {
            // Column source / upstream
            (NlQueryIntent.FindColumnSource,
             new[]{ "source of","where does","where do","come from","upstream","comes from" },
             "Column Source / Upstream"),

            // Column target / downstream
            (NlQueryIntent.FindColumnTarget,
             new[]{ "target","downstream","goes to","go to","flows to","destined to" },
             "Column Target / Downstream"),

            // Packages writing to table
            (NlQueryIntent.FindPackagesWritingToTable,
             new[]{ "write to","writes to","insert into","load to","save to" },
             "Packages Writing to Table"),

            // Packages reading from table
            (NlQueryIntent.FindPackagesReadingFromTable,
             new[]{ "read from","reads from","select from","source from" },
             "Packages Reading from Table"),

            // High fanout
            (NlQueryIntent.FindHighFanoutColumns,
             new[]{ "most used","most referenced","high fanout","most downstream" },
             "High-Fanout Columns"),

            // Orphan tables
            (NlQueryIntent.FindOrphanTables,
             new[]{ "no downstream","orphan","unused","dead end","no target" },
             "Orphan Tables (No Downstream)"),

            // List all packages
            (NlQueryIntent.FindAllPackages,
             new[]{ "list package","list packages","all packages","show packages" },
             "All Packages"),

            // Package using column
            (NlQueryIntent.FindPackageForColumn,
             new[]{ "which package uses","what package uses","who uses column" },
             "Packages Using Column"),

            // Table mappings (fallback)
            (NlQueryIntent.FindTableMappings,
             new[]{ "mapping","mapped","column map","lineage of","flow of","flow" },
             "Table/Column Mappings"),
        };

        public static ParsedNlQuery Parse(string query)
        {
            var q = (query ?? "").Trim();
            var lower = q.ToLowerInvariant();

            var result = new ParsedNlQuery { Original = q };

            // Detect intent
            foreach (var (intent, keywords, label) in _rules)
            {
                if (keywords.Any(k => lower.Contains(k)))
                {
                    result.Intent = intent;
                    result.IntentLabel = label;
                    break;
                }
            }

            // Extract threshold number (e.g. "more than 5 downstream")
            var numMatch = Regex.Match(lower, @"\b(\d+)\b");
            if (numMatch.Success && int.TryParse(numMatch.Value, out var n))
                result.Threshold = Math.Max(1, n);

            // Extract entity name — the "subject" after removing stop words
            result.Entity = ExtractEntity(q, lower, result.Intent);
            result.EntityType = InferEntityType(result.Entity, lower);

            return result;
        }

        private static string ExtractEntity(string q, string lower, NlQueryIntent intent)
        {
            // Strip out intent-keyword phrases to isolate the entity name
            var stripped = lower;
            foreach (var (_, keywords, _) in _rules)
                foreach (var k in keywords)
                    stripped = stripped.Replace(k, " ");

            // Strip common filler words (bilingual)
            var fillers = new[]{ "table","column","the","a","an","of","in","to","from","does","do","where",
                                 "is","are","have","has","that","this","?","'","\""};

            var tokens = Regex.Split(stripped.Trim(), @"\s+")
                .Where(t => t.Length > 0 && !fillers.Contains(t.ToLowerInvariant()))
                .ToArray();

            // Pick the longest remaining token as the entity candidate
            return tokens.OrderByDescending(t => t.Length).FirstOrDefault() ?? "";
        }

        private static string InferEntityType(string entity, string lower)
        {
            if (string.IsNullOrEmpty(entity)) return "";
            // Heuristic: entity with a dot is likely schema.table
            if (entity.Contains('.')) return "table";
            // If query mentions "column" or "field" explicitly → column
            if (lower.Contains("column") || lower.Contains("field")) return "column";
            return "table"; // default assumption
        }
    }

    // ── Query Engine ──────────────────────────────────────────────────────────

    public static class NlQueryEngine
    {
        /// <summary>Execute a parsed NL query against the live lineage graph.</summary>
        public static NlQueryResult Execute(ParsedNlQuery parsed, LineageGraph graph)
        {
            if (graph == null || graph.ColumnMappings.Count == 0)
            {
                return new NlQueryResult
                {
                    Query = parsed,
                    Summary = "Graph memory is empty. Please run a Lineage Scan first.",
                    Rows = new List<NlResultRow>(),
                    FollowUps = SuggestedQuestions(graph)
                };
            }

            // 1. Try explicit intent routing
            var result = parsed.Intent switch
            {
                NlQueryIntent.FindColumnSource           => FindColumnSource(parsed, graph),
                NlQueryIntent.FindColumnTarget           => FindColumnTarget(parsed, graph),
                NlQueryIntent.FindPackagesWritingToTable => FindPackagesWritingToTable(parsed, graph),
                NlQueryIntent.FindPackagesReadingFromTable => FindPackagesReadingFromTable(parsed, graph),
                NlQueryIntent.FindTableMappings          => FindTableMappings(parsed, graph),
                NlQueryIntent.FindHighFanoutColumns      => FindHighFanoutColumns(parsed, graph),
                NlQueryIntent.FindOrphanTables           => FindOrphanTables(parsed, graph),
                NlQueryIntent.FindPackageForColumn       => FindPackageForColumn(parsed, graph),
                NlQueryIntent.FindAllPackages            => FindAllPackages(parsed, graph),
                _                                        => null
            };

            // 2. Dynamic Fallback: If no intent matched or zero rows, search graph dynamically for matched tokens!
            if (result == null || !result.HasResults)
            {
                var dynamicSearch = DynamicGraphSearch(parsed, graph);
                if (dynamicSearch.HasResults)
                    return dynamicSearch;
            }

            if (result != null)
            {
                // Inject real dynamic follow-ups based on actual graph entities
                result.FollowUps = GenerateDynamicFollowUps(parsed, graph);
                return result;
            }

            return UnknownResult(parsed, graph);
        }

        // ── Dynamic Graph Search Fallback ────────────────────────────────────

        private static NlQueryResult DynamicGraphSearch(ParsedNlQuery parsed, LineageGraph graph)
        {
            var query = parsed.Original.ToLowerInvariant();
            var tokens = Regex.Split(query, @"\s+").Where(t => t.Length > 2).ToList();

            // Search for any column mapping matching any token in source/target table or column
            var matches = graph.ColumnMappings
                .Where(m => tokens.Any(t =>
                    (!string.IsNullOrEmpty(m.SourceTable) && m.SourceTable.Contains(t, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(m.TargetTable) && m.TargetTable.Contains(t, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(m.SourceColumnName) && m.SourceColumnName.Contains(t, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(m.TargetColumnName) && m.TargetColumnName.Contains(t, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(m.PackageId) && ResolvePackage(graph, m.PackageId).Contains(t, StringComparison.OrdinalIgnoreCase))
                ))
                .Select(m => new NlResultRow
                {
                    Primary   = FormatColumn(m.SourceSchema, m.SourceTable, m.SourceColumnName),
                    Secondary = FormatColumn(m.TargetSchema, m.TargetTable, m.TargetColumnName) + $" ({ResolvePackage(graph, m.PackageId)})",
                    Category  = m.OperationType ?? "Mapping",
                    Count     = 1
                })
                .DistinctBy(r => r.Primary + "|" + r.Secondary)
                .Take(50)
                .ToList();

            if (matches.Count > 0)
            {
                return new NlQueryResult
                {
                    Query   = parsed,
                    Summary = $"Dynamic graph search: Found {matches.Count} data flows related to \"{parsed.Original}\".",
                    Rows    = matches,
                    FollowUps = GenerateDynamicFollowUps(parsed, graph)
                };
            }

            return UnknownResult(parsed, graph);
        }

        // ── Dynamic Follow-Up & Suggested Questions ──────────────────────────

        public static List<string> SuggestedQuestions(LineageGraph? graph = null)
        {
            if (graph == null || graph.ColumnMappings.Count == 0)
            {
                return new List<string>
                {
                    "Where does OsPokok come from?",
                    "Which package writes to FactSimpanan?",
                    "Which package reads from MasterPinjaman?",
                    "Which column is used the most?",
                    "Which tables have no downstream?",
                    "List all packages"
                };
            }

            var rand = new Random();
            var tables = graph.ColumnMappings
                .Select(m => m.SourceTable)
                .Concat(graph.ColumnMappings.Select(m => m.TargetTable))
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => rand.Next())
                .Take(3)
                .ToList();

            var cols = graph.ColumnMappings
                .Select(m => m.SourceColumnName)
                .Concat(graph.ColumnMappings.Select(m => m.TargetColumnName))
                .Where(c => !string.IsNullOrEmpty(c) && c != "*")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => rand.Next())
                .Take(3)
                .ToList();

            var list = new List<string>();
            if (cols.Count > 0) list.Add($"Where does data {cols[0]} come from?");
            if (tables.Count > 0) list.Add($"Which package writes to {tables[0]}?");
            if (tables.Count > 1) list.Add($"Which package reads from {tables[1]}?");
            if (cols.Count > 1) list.Add($"Where does column {cols[1]} go to?");

            list.Add("Which column is used the most?");
            list.Add("Which tables have no downstream?");
            list.Add("List all packages");

            return list;
        }

        private static List<string> GenerateDynamicFollowUps(ParsedNlQuery parsed, LineageGraph graph)
        {
            var suggestions = SuggestedQuestions(graph);
            if (!string.IsNullOrEmpty(parsed.Entity))
            {
                suggestions.Insert(0, $"Where does {parsed.Entity} go to?");
                suggestions.Insert(1, $"Which package uses {parsed.Entity}?");
            }
            return suggestions.Distinct().Take(5).ToList();
        }

        private static NlQueryResult UnknownResult(ParsedNlQuery parsed, LineageGraph graph) => new()
        {
            Query   = parsed,
            Summary = $"Search \"{parsed.Original}\" found no specific entity. Try the questions below:",
            Rows    = new List<NlResultRow>(),
            FollowUps = SuggestedQuestions(graph)
        };

        // ── FindColumnSource ─────────────────────────────────────────────────

        private static NlQueryResult FindColumnSource(ParsedNlQuery parsed, LineageGraph graph)
        {
            var entity = parsed.Entity;
            var matches = graph.ColumnMappings
                .Where(m => MatchesEntity(m.TargetColumnName, m.TargetTable, entity))
                .Select(m => new NlResultRow
                {
                    Primary   = FormatColumn(m.SourceSchema, m.SourceTable, m.SourceColumnName),
                    Secondary = $"{ResolvePackage(graph, m.PackageId)} / {ResolveTask(graph, m.TaskId)}",
                    Category  = "Source",
                    Count     = 1
                })
                .DistinctBy(r => r.Primary + r.Secondary)
                .OrderBy(r => r.Primary)
                .ToList();

            return new NlQueryResult
            {
                Query   = parsed,
                Summary = matches.Count > 0
                    ? $"Found {matches.Count} upstream source(s) for \"{entity}\"."
                    : $"No source found for \"{entity}\". Check spelling or try a table name.",
                Rows    = matches,
                FollowUps = new List<string>
                {
                    $"Where does {entity} go to?",
                    $"Which package writes to {entity}?",
                    $"What mappings exist in {entity}?"
                }
            };
        }

        // ── FindColumnTarget ─────────────────────────────────────────────────

        private static NlQueryResult FindColumnTarget(ParsedNlQuery parsed, LineageGraph graph)
        {
            var entity = parsed.Entity;
            var matches = graph.ColumnMappings
                .Where(m => MatchesEntity(m.SourceColumnName, m.SourceTable, entity))
                .Select(m => new NlResultRow
                {
                    Primary   = FormatColumn(m.TargetSchema, m.TargetTable, m.TargetColumnName),
                    Secondary = $"{ResolvePackage(graph, m.PackageId)} / {ResolveTask(graph, m.TaskId)}",
                    Category  = "Target",
                    Count     = 1
                })
                .DistinctBy(r => r.Primary + r.Secondary)
                .OrderBy(r => r.Primary)
                .ToList();

            return new NlQueryResult
            {
                Query   = parsed,
                Summary = matches.Count > 0
                    ? $"Found {matches.Count} downstream target(s) for \"{entity}\"."
                    : $"No downstream found for \"{entity}\".",
                Rows    = matches,
                FollowUps = new List<string>
                {
                    $"Where does {entity} come from?",
                    $"Which package uses column {entity}?",
                }
            };
        }

        // ── FindPackagesWritingToTable ────────────────────────────────────────

        private static NlQueryResult FindPackagesWritingToTable(ParsedNlQuery parsed, LineageGraph graph)
        {
            var entity = parsed.Entity;
            var rows = graph.ColumnMappings
                .Where(m => MatchesTable(m.TargetSchema, m.TargetTable, entity))
                .GroupBy(m => m.PackageId)
                .Select(g =>
                {
                    var pkg = ResolvePackage(graph, g.Key);
                    var tasks = g.Select(m => ResolveTask(graph, m.TaskId))
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .OrderBy(t => t);
                    return new NlResultRow
                    {
                        Primary   = pkg,
                        Secondary = string.Join(", ", tasks),
                        Category  = "Package",
                        Count     = g.Count()
                    };
                })
                .OrderBy(r => r.Primary)
                .ToList();

            return new NlQueryResult
            {
                Query   = parsed,
                Summary = rows.Count > 0
                    ? $"{rows.Count} package(s) write to table \"{entity}\"."
                    : $"No packages write to \"{entity}\".",
                Rows    = rows,
                FollowUps = new List<string>
                {
                    $"Which package reads from {entity}?",
                    $"What mappings exist in {entity}?",
                    $"Where does data {entity} come from?"
                }
            };
        }

        // ── FindPackagesReadingFromTable ──────────────────────────────────────

        private static NlQueryResult FindPackagesReadingFromTable(ParsedNlQuery parsed, LineageGraph graph)
        {
            var entity = parsed.Entity;
            var rows = graph.ColumnMappings
                .Where(m => MatchesTable(m.SourceSchema, m.SourceTable, entity))
                .GroupBy(m => m.PackageId)
                .Select(g =>
                {
                    var pkg = ResolvePackage(graph, g.Key);
                    var tasks = g.Select(m => ResolveTask(graph, m.TaskId))
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .OrderBy(t => t);
                    return new NlResultRow
                    {
                        Primary   = pkg,
                        Secondary = string.Join(", ", tasks),
                        Category  = "Package",
                        Count     = g.Count()
                    };
                })
                .OrderBy(r => r.Primary)
                .ToList();

            return new NlQueryResult
            {
                Query   = parsed,
                Summary = rows.Count > 0
                    ? $"{rows.Count} package(s) read from table \"{entity}\"."
                    : $"No packages read from \"{entity}\".",
                Rows    = rows,
                FollowUps = new List<string>
                {
                    $"Which package writes to {entity}?",
                    $"Where does data from {entity} go to?",
                }
            };
        }

        // ── FindTableMappings ─────────────────────────────────────────────────

        private static NlQueryResult FindTableMappings(ParsedNlQuery parsed, LineageGraph graph)
        {
            var entity = parsed.Entity;
            var rows = graph.ColumnMappings
                .Where(m => MatchesTable(m.SourceSchema, m.SourceTable, entity)
                         || MatchesTable(m.TargetSchema, m.TargetTable, entity)
                         || MatchesEntity(m.SourceColumnName, m.SourceTable, entity)
                         || MatchesEntity(m.TargetColumnName, m.TargetTable, entity))
                .Select(m => new NlResultRow
                {
                    Primary   = FormatColumn(m.SourceSchema, m.SourceTable, m.SourceColumnName),
                    Secondary = FormatColumn(m.TargetSchema, m.TargetTable, m.TargetColumnName),
                    Category  = m.OperationType ?? "Map",
                    Count     = 1
                })
                .DistinctBy(r => r.Primary + "|" + r.Secondary)
                .OrderBy(r => r.Primary)
                .Take(100)
                .ToList();

            return new NlQueryResult
            {
                Query   = parsed,
                Summary = rows.Count > 0
                    ? $"Found {rows.Count} column mapping(s) involving \"{entity}\"."
                    : $"No mappings found for \"{entity}\".",
                Rows    = rows,
                FollowUps = new List<string>
                {
                    $"Which package writes to {entity}?",
                    $"Which package reads from {entity}?",
                    $"Where does {entity} come from?"
                }
            };
        }

        // ── FindHighFanoutColumns ─────────────────────────────────────────────

        private static NlQueryResult FindHighFanoutColumns(ParsedNlQuery parsed, LineageGraph graph)
        {
            var threshold = parsed.Threshold;
            var rows = graph.ColumnMappings
                .Where(m => !string.IsNullOrEmpty(m.SourceColumnName) && m.SourceColumnName != "*")
                .GroupBy(m => FormatColumn(m.SourceSchema, m.SourceTable, m.SourceColumnName),
                         StringComparer.OrdinalIgnoreCase)
                .Select(g => new NlResultRow
                {
                    Primary   = g.Key,
                    Secondary = $"Used in {g.Count()} mapping(s)",
                    Category  = "Column",
                    Count     = g.Count()
                })
                .Where(r => r.Count >= threshold)
                .OrderByDescending(r => r.Count)
                .Take(30)
                .ToList();

            return new NlQueryResult
            {
                Query   = parsed,
                Summary = rows.Count > 0
                    ? $"Top {rows.Count} columns with {threshold}+ downstream mappings."
                    : $"No columns with {threshold}+ downstream mappings found.",
                Rows    = rows,
                FollowUps = new List<string>
                {
                    "Which column has no downstream?",
                    "Which tables have no downstream?",
                }
            };
        }

        // ── FindOrphanTables ──────────────────────────────────────────────────

        private static NlQueryResult FindOrphanTables(ParsedNlQuery parsed, LineageGraph graph)
        {
            var sources = graph.ColumnMappings
                .Where(m => !string.IsNullOrEmpty(m.SourceTable))
                .Select(m => FormatTable(m.SourceSchema, m.SourceTable))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var targets = graph.ColumnMappings
                .Where(m => !string.IsNullOrEmpty(m.TargetTable))
                .Select(m => FormatTable(m.TargetSchema, m.TargetTable))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Orphans = tables that appear ONLY as a target (no downstream as a source)
            var orphans = targets.Except(sources, StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t)
                .ToList();

            var rows = orphans.Select(t => new NlResultRow
            {
                Primary   = t,
                Secondary = "No downstream targets found",
                Category  = "Table",
                Count     = 0
            }).ToList();

            return new NlQueryResult
            {
                Query   = parsed,
                Summary = rows.Count > 0
                    ? $"Found {rows.Count} table(s) with no downstream consumers — possible final targets or dead-ends."
                    : "All tables have at least one downstream consumer.",
                Rows    = rows,
                FollowUps = new List<string>
                {
                    "Which column is used the most?",
                    "List all packages",
                }
            };
        }

        // ── FindPackageForColumn ──────────────────────────────────────────────

        private static NlQueryResult FindPackageForColumn(ParsedNlQuery parsed, LineageGraph graph)
        {
            var entity = parsed.Entity;
            var rows = graph.ColumnMappings
                .Where(m => MatchesEntity(m.SourceColumnName, "", entity)
                         || MatchesEntity(m.TargetColumnName, "", entity))
                .GroupBy(m => m.PackageId)
                .Select(g => new NlResultRow
                {
                    Primary   = ResolvePackage(graph, g.Key),
                    Secondary = string.Join(", ", g.Select(m => ResolveTask(graph, m.TaskId)).Distinct().OrderBy(t => t)),
                    Category  = "Package",
                    Count     = g.Count()
                })
                .OrderBy(r => r.Primary)
                .ToList();

            return new NlQueryResult
            {
                Query   = parsed,
                Summary = rows.Count > 0
                    ? $"{rows.Count} package(s) reference column \"{entity}\"."
                    : $"No packages reference column \"{entity}\".",
                Rows    = rows,
                FollowUps = new List<string>
                {
                    $"Where does {entity} come from?",
                    $"Where does {entity} go to?",
                }
            };
        }

        // ── FindAllPackages ───────────────────────────────────────────────────

        private static NlQueryResult FindAllPackages(ParsedNlQuery parsed, LineageGraph graph)
        {
            var rows = graph.Packages
                .Select(p =>
                {
                    var taskCount = graph.Tasks.Count(t => t.PackageId == p.Id);
                    var mapCount  = graph.ColumnMappings.Count(m => m.PackageId == p.Id);
                    return new NlResultRow
                    {
                        Primary   = p.Name,
                        Secondary = $"{taskCount} task(s) · {mapCount} column mapping(s)",
                        Category  = "Package",
                        Count     = mapCount
                    };
                })
                .OrderBy(r => r.Primary)
                .ToList();

            return new NlQueryResult
            {
                Query   = parsed,
                Summary = $"Project has {rows.Count} SSIS package(s).",
                Rows    = rows,
                FollowUps = new List<string>
                {
                    "Which column is used the most?",
                    "Which tables have no downstream?",
                }
            };
        }

        // ── Unknown ───────────────────────────────────────────────────────────

        private static NlQueryResult UnknownResult(ParsedNlQuery parsed) => new()
        {
            Query   = parsed,
            Summary = "Couldn't understand the query. Try one of the suggested questions below.",
            Rows    = new List<NlResultRow>(),
            FollowUps = SuggestedQuestions()
        };

        // ── Suggested questions for cold start ───────────────────────────────

        public static List<string> SuggestedQuestions() => new()
        {
            "Where does OsPokok come from?",
            "Which package writes to FactSimpanan?",
            "Which package reads from MasterPinjaman?",
            "Where does column NamaAnggota go to?",
            "Which column is used the most?",
            "Which tables have no downstream?",
            "List all packages",
            "Which package uses column IdAnggota?",
        };

        // ── Helpers ───────────────────────────────────────────────────────────

        private static bool MatchesTable(string schema, string table, string target)
        {
            if (string.IsNullOrEmpty(target)) return false;
            var tl = target.ToLowerInvariant();

            if (!string.IsNullOrEmpty(table))
            {
                var qualified = string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";
                if (qualified.Equals(tl, StringComparison.OrdinalIgnoreCase)) return true;
                if (table.Equals(tl, StringComparison.OrdinalIgnoreCase)) return true;
                if (table.Contains(tl, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool MatchesEntity(string column, string table, string target)
        {
            if (string.IsNullOrEmpty(target)) return false;
            if (!string.IsNullOrEmpty(column) && column.Equals(target, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(table)  && table.Equals(target, StringComparison.OrdinalIgnoreCase))  return true;
            if (!string.IsNullOrEmpty(column) && column.Contains(target, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(table)  && table.Contains(target, StringComparison.OrdinalIgnoreCase))  return true;
            return false;
        }

        private static string FormatColumn(string schema, string table, string column)
        {
            var t = string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";
            return string.IsNullOrEmpty(column) ? t : $"{t}.{column}";
        }

        private static string FormatTable(string schema, string table) =>
            string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";

        private static string ResolvePackage(LineageGraph g, string id) =>
            g.Packages.Find(p => p.Id == id)?.Name ?? id;

        private static string ResolveTask(LineageGraph g, string id) =>
            g.Tasks.Find(t => t.Id == id)?.Name ?? id;
    }
}
