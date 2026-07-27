using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

namespace SsisLineage.UI.Services
{
    public static class SqlFormatter
    {
        private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "FROM", "WHERE", "JOIN", "INNER", "LEFT", "RIGHT", "OUTER", "CROSS", "FULL", "ON",
            "GROUP", "BY", "ORDER", "HAVING", "LIMIT", "TOP", "AS", "AND", "OR", "NOT", "IN", "IS", "NULL",
            "LIKE", "EXISTS", "BETWEEN", "CASE", "WHEN", "THEN", "ELSE", "END", "UNION", "ALL",
            "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE", "MERGE", "USING", "MATCHED",
            "WITH", "OVER", "PARTITION", "ASC", "DESC", "EXEC", "EXECUTE", "DISTINCT"
        };

        private static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase)
        {
            "COUNT", "SUM", "AVG", "MAX", "MIN", "COALESCE", "ISNULL", "CAST", "CONVERT",
            "ROW_NUMBER", "RANK", "DENSE_RANK", "GETDATE", "DATEADD", "DATEDIFF", "SUBSTRING",
            "REPLACE", "CHARINDEX", "LEN", "UPPER", "LOWER", "TRIM", "LTRIM", "RTRIM", "CONCAT",
            "STUFF", "NULLIF", "ABS", "ROUND", "FLOOR", "CEILING"
        };

        /// <summary>
        /// Formats single-line or messy SQL queries into pretty multi-line formatted SQL.
        /// </summary>
        public static string FormatSql(string rawSql)
        {
            if (string.IsNullOrWhiteSpace(rawSql)) return "";

            // If it's not a SQL query, return cleaned string
            if (!rawSql.Contains("SELECT", StringComparison.OrdinalIgnoreCase) &&
                !rawSql.Contains("INSERT", StringComparison.OrdinalIgnoreCase) &&
                !rawSql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase) &&
                !rawSql.Contains("EXEC", StringComparison.OrdinalIgnoreCase) &&
                !rawSql.Contains("MERGE", StringComparison.OrdinalIgnoreCase) &&
                !rawSql.Contains("WITH", StringComparison.OrdinalIgnoreCase))
            {
                return rawSql.Trim();
            }

            var sql = rawSql.Replace("\r\n", "\n").Replace("\r", "\n").Trim();

            // Match all major clauses in a SINGLE regex pass to avoid splitting "INNER JOIN" into "INNER\nJOIN"
            var clausePattern = @"(?i)\b(SELECT|FROM|INNER\s+JOIN|LEFT\s+OUTER\s+JOIN|RIGHT\s+OUTER\s+JOIN|FULL\s+OUTER\s+JOIN|LEFT\s+JOIN|RIGHT\s+JOIN|FULL\s+JOIN|CROSS\s+JOIN|JOIN|WHERE|GROUP\s+BY|ORDER\s+BY|HAVING|INSERT\s+INTO|VALUES|UPDATE|SET|MERGE\s+INTO|MERGE|USING|WHEN\s+MATCHED\s+THEN|WHEN\s+NOT\s+MATCHED\s+THEN|UNION\s+ALL|UNION|WITH)\b";
            sql = Regex.Replace(sql, clausePattern, m => "\n" + Regex.Replace(m.Value, @"\s+", " ").ToUpperInvariant());

            // Indent AND / OR under WHERE / ON
            sql = Regex.Replace(sql, @"(?i)\b(AND|OR)\b", "\n  $1");

            // Format SELECT column lists onto separate indented lines
            sql = Regex.Replace(sql, @"(?i)\bSELECT\b\s*", "SELECT\n  ");
            sql = Regex.Replace(sql, @",\s*(?=[a-zA-Z0-9_\[\]`""\.\*\()]+)", ",\n  ");

            // Clean up lines
            var lines = sql.Split('\n');
            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                var trimmed = line.TrimEnd();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;
                sb.AppendLine(trimmed);
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Formats SQL and wraps tokens in inline styled HTML spans for syntax highlighting.
        /// </summary>
        public static MarkupString HighlightSqlToHtml(string rawSql)
        {
            if (string.IsNullOrWhiteSpace(rawSql)) return new MarkupString("");

            var formatted = FormatSql(rawSql);

            // Tokenize SQL string using Regex
            var pattern = @"('(''|[^'])*')|(--[^\n]*)|(/\*[\s\S]*?\*/)|(\b[a-zA-Z_][a-zA-Z0-9_]*\b)|([0-9]+(?:\.[0-9]+)?)|([<>=!]+|[\+\-\*/%])|(\n|\s+|.)";
            var matches = Regex.Matches(formatted, pattern);

            var html = new StringBuilder();

            foreach (Match m in matches)
            {
                var text = m.Value;

                if (string.IsNullOrEmpty(text)) continue;

                if (text.StartsWith("'") && text.EndsWith("'"))
                {
                    // String literal (Emerald Green)
                    html.Append($"<span style=\"color: #4ade80 !important; font-weight: 500;\">{System.Net.WebUtility.HtmlEncode(text)}</span>");
                }
                else if (text.StartsWith("--") || text.StartsWith("/*"))
                {
                    // Comment (Muted Grey)
                    html.Append($"<span style=\"color: #94a3b8 !important; font-style: italic;\">{System.Net.WebUtility.HtmlEncode(text)}</span>");
                }
                else if (char.IsDigit(text[0]))
                {
                    // Number (Orange)
                    html.Append($"<span style=\"color: #fb923c !important;\">{System.Net.WebUtility.HtmlEncode(text)}</span>");
                }
                else if (Keywords.Contains(text))
                {
                    // SQL Keyword (Bright Cyan Bold)
                    html.Append($"<span style=\"color: #38bdf8 !important; font-weight: 700;\">{System.Net.WebUtility.HtmlEncode(text.ToUpperInvariant())}</span>");
                }
                else if (Functions.Contains(text))
                {
                    // SQL Function (Vibrant Gold)
                    html.Append($"<span style=\"color: #facc15 !important; font-weight: 600;\">{System.Net.WebUtility.HtmlEncode(text.ToUpperInvariant())}</span>");
                }
                else if (text == "\n")
                {
                    html.Append("\n");
                }
                else
                {
                    // Identifiers / Tables / Columns (Light Slate)
                    html.Append($"<span style=\"color: #e2e8f0 !important;\">{System.Net.WebUtility.HtmlEncode(text)}</span>");
                }
            }

            return new MarkupString(html.ToString());
        }
    }
}
