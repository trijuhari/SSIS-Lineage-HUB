using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SsisLineage.Core
{
    public class BusinessGlossary
    {
        private readonly Dictionary<string, string> _dictionary = new(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, string> DefaultGlossary = new(StringComparer.OrdinalIgnoreCase)
        {
            { "TRX_HARIAN", "Transaksi Harian" },
            { "MASTER_CABANG", "Master Cabang" },
            { "KODE_CAB", "Kode Cabang" },
            { "STATUS", "Status Transaksi" },
            { "FCT_KINERJA_HARIAN", "Kinerja Harian Fakta" },
            { "TRX", "Transaksi" },
            { "HARIAN", "Harian" },
            { "CABANG", "Cabang" },
            { "KODE", "Kode" },
            { "MASTER", "Master" },
            { "FCT", "Fakta" },
            { "DIM", "Dimensi" },
            { "STG", "Staging" },
            { "CUST", "Pelanggan" },
            { "PROD", "Produk" },
            { "AMT", "Jumlah/Nilai" },
            { "DATE", "Tanggal" },
            { "TANGGAL", "Tanggal" },
            { "PELANGGAN", "Pelanggan" },
            { "PRODUK", "Produk" },
            { "NAMA", "Nama" },
            { "ID", "Identitas" },
            { "APPROVED", "Disetujui" }
        };

        public BusinessGlossary()
        {
            // Seed with defaults
            foreach (var kv in DefaultGlossary)
            {
                _dictionary[kv.Key] = kv.Value;
            }
        }

        public BusinessGlossary(Dictionary<string, string> customGlossary) : this()
        {
            if (customGlossary != null)
            {
                foreach (var kv in customGlossary)
                {
                    _dictionary[kv.Key] = kv.Value;
                }
            }
        }

        public Dictionary<string, string> GetTerms() => _dictionary;

        public static BusinessGlossary Load(string projectDirectory)
        {
            if (string.IsNullOrEmpty(projectDirectory))
            {
                return new BusinessGlossary();
            }

            try
            {
                var glossaryPath = Path.Combine(projectDirectory, "glossary.json");
                if (File.Exists(glossaryPath))
                {
                    var json = File.ReadAllText(glossaryPath);
                    var custom = JsonSerializer.Deserialize<Dictionary<string, string>>(json, 
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (custom != null)
                    {
                        return new BusinessGlossary(custom);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Failed to load glossary.json: {ex.Message}");
            }

            return new BusinessGlossary();
        }

        /// <summary>
        /// Translates a table or column name. If includePhysical is true, returns "Definisi Bisnis (PHYSICAL_NAME)".
        /// </summary>
        public string Translate(string name, bool includePhysical = true)
        {
            if (string.IsNullOrWhiteSpace(name)) return name;

            var cleanName = name.Trim('[', ']', ' ', '"', '`').ToUpperInvariant();
            
            // Remove schema prefix if present for lookup, e.g. "dbo.TRX_HARIAN" -> "TRX_HARIAN"
            var lastDot = cleanName.LastIndexOf('.');
            var lookupKey = lastDot >= 0 ? cleanName[(lastDot + 1)..] : cleanName;

            if (_dictionary.TryGetValue(lookupKey, out var businessName))
            {
                return includePhysical ? $"`{businessName}` (`{name}`)" : $"`{businessName}`";
            }

            // Fallback: check if we can split by underscores and translate parts
            var parts = lookupKey.Split('_');
            var translatedParts = new List<string>();
            bool matchedAny = false;
            foreach (var part in parts)
            {
                if (_dictionary.TryGetValue(part, out var partBusinessName))
                {
                    translatedParts.Add(partBusinessName);
                    matchedAny = true;
                }
                else
                {
                    translatedParts.Add(part);
                }
            }

            if (matchedAny)
            {
                var combined = string.Join(" ", translatedParts);
                return includePhysical ? $"`{combined}` (`{name}`)" : $"`{combined}`";
            }

            return $"`{name}`";
        }

        /// <summary>
        /// Translates names inside expressions or condition strings (like join/filter details).
        /// </summary>
        public string TranslateExpression(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression)) return expression;

            var result = expression;
            // Simple replace of known glossary keys in the expression
            // Order by length descending to avoid partial matches
            var sortedKeys = new List<string>(_dictionary.Keys);
            sortedKeys.Sort((a, b) => b.Length.CompareTo(a.Length));

            foreach (var key in sortedKeys)
            {
                // Word boundary lookup or simple replace with a safe check
                var businessName = _dictionary[key];
                
                // Replace case-insensitively
                int index = 0;
                while ((index = result.IndexOf(key, index, StringComparison.OrdinalIgnoreCase)) != -1)
                {
                    // Check if it's a whole word or surrounded by brackets/dots/spaces
                    bool isStartBoundary = index == 0 || !char.IsLetterOrDigit(result[index - 1]) && result[index - 1] != '_';
                    bool isEndBoundary = (index + key.Length) == result.Length || !char.IsLetterOrDigit(result[index + key.Length]) && result[index + key.Length] != '_';

                    if (isStartBoundary && isEndBoundary)
                    {
                        var replacement = $"{businessName} ({result.Substring(index, key.Length)})";
                        result = result.Remove(index, key.Length).Insert(index, replacement);
                        index += replacement.Length;
                    }
                    else
                    {
                        index += key.Length;
                    }
                }
            }

            return result;
        }
    }
}
