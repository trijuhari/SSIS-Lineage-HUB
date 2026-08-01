namespace SsisLineage.UI.Services
{
    public class LanguageService
    {
        public event Action? OnLanguageChanged;

        private bool _isEnglish = true;
        public bool IsEnglish
        {
            get => _isEnglish;
            set
            {
                if (_isEnglish != value)
                {
                    _isEnglish = value;
                    OnLanguageChanged?.Invoke();
                }
            }
        }

        public void ToggleLanguage()
        {
            IsEnglish = !IsEnglish;
        }

        private readonly Dictionary<string, string> _translations = new(StringComparer.OrdinalIgnoreCase)
        {
            {"DISCOVERY", "PENEMUAN"},
            {"Lineage Discovery", "Penemuan Lineage"},
            {"Lineage Search", "Pencarian Lineage"},
            {"Detailed Report", "Laporan Detail"},
            {"ANALYSIS & GOVERNANCE", "ANALISIS & TATA KELOLA"},
            {"SSIS Inspector", "Inspektur SSIS"},
            {"Code Migrator", "Migrasi Kode"},
            {"What-If Simulator", "Simulator What-If"},
            {"NL Query Engine", "Mesin Query NL"},
            {"Risk Scorer & Heatmap", "Skor Risiko & Heatmap"},
            {"Quality Propagation", "Propagasi Kualitas"},
            {"Data Contracts", "Kontrak Data"},
            
            // Landing page
            {"The Ultimate SSIS Lineage", "Penelusuran SSIS Terlengkap"},
            {"Discovery Hub", "Pusat Penemuan"},
            {"Instantly visualize, trace, and govern your SQL Server Integration Services pipelines with an offline-first, developer-focused platform.", "Visualisasikan, lacak, dan kelola pipeline SQL Server Integration Services Anda secara instan dengan platform offline yang berfokus pada developer."},
            {"Upload Project", "Unggah Proyek"},
            {"Load Samples", "Muat Sampel"},
            {"How it works", "Cara Kerja"},
            
            // Main layout
            {"Search commands, entities...", "Cari perintah, entitas..."},
            {"WORKSPACE", "RUANG KERJA"},
            
            // NL Query Page
            {"Natural Language", "Bahasa Alami"},
            {"Query Engine", "Mesin Query"},
            {"Ask anything about your data lineage — in English or Indonesian.", "Tanyakan apa saja tentang lineage data Anda — dalam Bahasa Inggris atau Indonesia."},
            {"No lineage report loaded", "Laporan lineage belum dimuat"},
            {"Generate or load a lineage report first, then return here to start querying.", "Buat atau muat laporan lineage terlebih dahulu, lalu kembali ke sini untuk mulai melakukan query."},
            {"Go to Workspace", "Ke Ruang Kerja"},
            {"Ask something about your lineage…", "Tanyakan sesuatu tentang lineage Anda…"},
            {"e.g., \"Where does OsPokok come from?\" or \"Which package writes to FactSimpanan?\"", "contoh: \"Dimana OsPokok berasal?\" atau \"Package mana yang nulis ke FactSimpanan?\""},
            {"Ask", "Tanya"},
            {"Frequently asked questions:", "Pertanyaan yang sering diajukan:"}
        };

        public string Translate(string englishText)
        {
            if (IsEnglish) return englishText;
            return _translations.TryGetValue(englishText, out var translated) ? translated : englishText;
        }

        public string Get(string englishText) => Translate(englishText);
    }
}
