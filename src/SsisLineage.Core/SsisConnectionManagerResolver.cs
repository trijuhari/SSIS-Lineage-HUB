using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace SsisLineage.Core
{
    /// <summary>
    /// Resolves connection strings from SSIS project .conmgr files, embedded package .dtsx files, and user overrides.
    /// </summary>
    public class SsisConnectionManagerResolver
    {
        private readonly Dictionary<string, string> _connectionStrings = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, string> ConnectionStrings => _connectionStrings;

        public SsisConnectionManagerResolver()
        {
        }

        public SsisConnectionManagerResolver(string? projectDirectory, IDictionary<string, string>? overrides = null)
        {
            if (overrides != null)
            {
                foreach (var (k, v) in overrides)
                {
                    _overrides[k] = v;
                    _connectionStrings[k] = v;
                }
            }

            if (!string.IsNullOrWhiteSpace(projectDirectory) && Directory.Exists(projectDirectory))
            {
                ScanDirectory(projectDirectory);
            }
        }

        public void AddConnection(string nameOrId, string connectionString)
        {
            if (string.IsNullOrWhiteSpace(nameOrId) || string.IsNullOrWhiteSpace(connectionString)) return;
            _connectionStrings[nameOrId] = connectionString;
        }

        public string? TryResolveFirstSqlConnectionString()
        {
            return _overrides.Values.FirstOrDefault(LooksLikeSqlConnection)
                ?? _connectionStrings.Values.FirstOrDefault(LooksLikeSqlConnection);
        }

        public string? TryResolveConnectionString(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                return null;
            }

            if (_overrides.TryGetValue(reference, out var overrideValue) && !string.IsNullOrWhiteSpace(overrideValue))
            {
                return overrideValue;
            }

            if (_connectionStrings.TryGetValue(reference, out var exactMatch) && !string.IsNullOrWhiteSpace(exactMatch))
            {
                return exactMatch;
            }

            var cleanName = ExtractConnectionManagerName(reference);
            if (!string.IsNullOrWhiteSpace(cleanName))
            {
                if (_overrides.TryGetValue(cleanName, out overrideValue) && !string.IsNullOrWhiteSpace(overrideValue))
                {
                    return overrideValue;
                }

                if (_connectionStrings.TryGetValue(cleanName, out var match) && !string.IsNullOrWhiteSpace(match))
                {
                    return match;
                }

                var fuzzy = _connectionStrings.FirstOrDefault(kv =>
                    kv.Key.Contains(cleanName, StringComparison.OrdinalIgnoreCase) ||
                    cleanName.Contains(kv.Key, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(fuzzy.Value))
                {
                    return fuzzy.Value;
                }
            }

            if (LooksLikeSqlConnection(reference))
            {
                return reference;
            }

            return null;
        }

        public static string? FindConnectionString(XElement element)
        {
            XNamespace dts = "www.microsoft.com/SqlServer/Dts";

            var attrNames = new[] { "ConnectionString", "ConnectionManagerConnectionString", "CreationName" };
            foreach (var attrName in attrNames)
            {
                var dtsAttr = element.Attribute(dts + attrName);
                if (dtsAttr != null && LooksLikeSqlConnection(dtsAttr.Value))
                    return dtsAttr.Value;

                var plainAttr = element.Attribute(attrName);
                if (plainAttr != null && LooksLikeSqlConnection(plainAttr.Value))
                    return plainAttr.Value;
            }

            var connStrProp = element.Descendants(dts + "Property")
                .FirstOrDefault(p => string.Equals(p.Attribute(dts + "Name")?.Value, "ConnectionString", StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(p.Attribute("Name")?.Value, "ConnectionString", StringComparison.OrdinalIgnoreCase));
            if (connStrProp != null && LooksLikeSqlConnection(connStrProp.Value))
                return connStrProp.Value;

            var objData = element.Element(dts + "ObjectData") ?? element.Element("ObjectData");
            if (objData != null)
            {
                foreach (var descendant in objData.Descendants())
                {
                    foreach (var attr in descendant.Attributes())
                    {
                        if (LooksLikeSqlConnection(attr.Value))
                            return attr.Value;
                    }
                    if (LooksLikeSqlConnection(descendant.Value))
                        return descendant.Value;
                }
            }

            foreach (var desc in element.Descendants())
            {
                foreach (var attr in desc.Attributes())
                {
                    if (LooksLikeSqlConnection(attr.Value))
                        return attr.Value;
                }
            }

            return null;
        }

        public static string? ExtractConnectionManagerName(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                return null;
            }

            var trimmed = reference.Trim();
            var bracketStart = trimmed.IndexOf('[');
            var bracketEnd = trimmed.LastIndexOf(']');
            if (bracketStart >= 0 && bracketEnd > bracketStart)
            {
                return trimmed.Substring(bracketStart + 1, bracketEnd - bracketStart - 1).Trim();
            }

            if (trimmed.Contains('\\'))
            {
                return trimmed.Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            }

            return trimmed;
        }

        private void ScanDirectory(string directory)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.conmgr", SearchOption.AllDirectories))
                {
                    TryLoadConmgrFile(file);
                }

                foreach (var file in Directory.EnumerateFiles(directory, "*.dtsx", SearchOption.AllDirectories))
                {
                    TryLoadDtsxFile(file);
                }
            }
            catch
            {
                // Best-effort directory scan
            }
        }

        private void TryLoadConmgrFile(string path)
        {
            try
            {
                var doc = XDocument.Load(path);
                var root = doc.Root;
                if (root == null) return;

                XNamespace dts = "www.microsoft.com/SqlServer/Dts";

                var objectName = root.Attribute(dts + "ObjectName")?.Value
                    ?? root.Attribute("ObjectName")?.Value
                    ?? Path.GetFileNameWithoutExtension(path);
                var dtsId = root.Attribute(dts + "DTSID")?.Value ?? root.Attribute("DTSID")?.Value;

                var connStr = FindConnectionString(root);
                if (string.IsNullOrWhiteSpace(connStr)) return;

                if (!string.IsNullOrWhiteSpace(objectName))
                {
                    _connectionStrings[objectName] = connStr;
                }
                if (!string.IsNullOrWhiteSpace(dtsId))
                {
                    _connectionStrings[dtsId] = connStr;
                }
                var fileName = Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    _connectionStrings[fileName] = connStr;
                }
            }
            catch
            {
                // Ignore malformed files during discovery
            }
        }

        private void TryLoadDtsxFile(string path)
        {
            try
            {
                var doc = XDocument.Load(path);
                XNamespace dts = "www.microsoft.com/SqlServer/Dts";

                foreach (var cm in doc.Descendants(dts + "ConnectionManager"))
                {
                    var objectName = cm.Attribute(dts + "ObjectName")?.Value
                        ?? cm.Attribute("ObjectName")?.Value;
                    var dtsId = cm.Attribute(dts + "DTSID")?.Value ?? cm.Attribute("DTSID")?.Value;
                    var refId = cm.Attribute(dts + "refId")?.Value ?? cm.Attribute("refId")?.Value;

                    var connStr = FindConnectionString(cm);
                    if (string.IsNullOrWhiteSpace(connStr)) continue;

                    if (!string.IsNullOrWhiteSpace(objectName) && !_connectionStrings.ContainsKey(objectName))
                    {
                        _connectionStrings[objectName] = connStr;
                    }
                    if (!string.IsNullOrWhiteSpace(dtsId) && !_connectionStrings.ContainsKey(dtsId))
                    {
                        _connectionStrings[dtsId] = connStr;
                    }
                    if (!string.IsNullOrWhiteSpace(refId) && !_connectionStrings.ContainsKey(refId))
                    {
                        _connectionStrings[refId] = connStr;
                    }
                }
            }
            catch
            {
                // Best-effort
            }
        }

        private static bool LooksLikeSqlConnection(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var lower = value.ToLowerInvariant();
            return lower.Contains("data source=")
                || lower.Contains("server=")
                || lower.Contains("initial catalog=")
                || lower.Contains("database=");
        }
    }
}
