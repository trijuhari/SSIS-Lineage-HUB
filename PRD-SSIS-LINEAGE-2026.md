# Product Requirements Document
## SSIS Lineage & Business Documentation Tool

| | |
|---|---|
| **Dokumen ID** | PRD-SSIS-LINEAGE-2026-001 |
| **Status** | Draft |
| **Owner** | Tri (Data Engineer) |
| **Terakhir diperbarui** | 25 Juli 2026 |
| **Terkait** | SSIS Modernization Journey 2026 |

---

## 1. Latar Belakang

Project SSIS di lingkungan data warehouse saat ini (SQL Server + SSIS package `.dtsx`) menyimpan business logic penting dalam bentuk campuran: control flow visual, embedded SQL di dalam task, dan stored procedure di database. Pengetahuan tentang **kenapa** sebuah pipeline dibangun seperti itu — bukan cuma **apa** yang dilakukannya — sering kali hanya ada di kepala satu-dua orang yang membangunnya.

Tidak ada dokumentasi hidup yang menjelaskan alur data dan logika bisnis di balik query secara otomatis. Dokumentasi manual cepat basi begitu pipeline berubah, dan proses onboarding/knowledge transfer bergantung penuh pada penjelasan lisan dari orang yang bersangkutan.

Ada tool referensi open-source ([SSIS-Project-Documentation](https://github.com/okutue/SSIS-Project-Documentation)) yang sudah menyelesaikan bagian *structural lineage* (parsing `.dtsx`/`.dtproj`, graph object & column-level, export ke berbagai format) dengan baik, tapi berhenti di level teknis — tidak menerjemahkan query menjadi penjelasan bisnis yang bisa dibaca orang non-teknis, dan bergantung pada runtime SSIS di Windows.

## 2. Masalah yang Diselesaikan

1. Tidak ada cara cepat untuk memahami alur data & logika bisnis suatu pipeline SSIS tanpa membaca satu per satu package secara manual.
2. Knowledge transfer bergantung pada orang, bukan dokumen — berisiko tinggi kalau engineer yang bersangkutan resign atau pindah tim.
3. Istilah kolom/tabel di database lawas banyak yang berupa singkatan internal, sulit dipahami tanpa konteks.
4. Tool referensi yang ada butuh runtime SSIS (Windows-only), yang menyulitkan penggunaan di lingkungan yang serba terbatas aksesnya.
5. Solusi berbasis LLM berisiko untuk data internal perbankan yang sensitif, dan hasilnya tidak deterministik.

## 3. Tujuan (Goals)

- Membangun tool yang membaca `.dtsx` secara langsung (parsing XML), tanpa bergantung pada runtime SSIS.
- Menghasilkan **data lineage** (table-to-table, hingga level kolom) secara otomatis dari isi package.
- Menghasilkan **narasi bisnis** dari query SQL secara otomatis menggunakan pendekatan **rule-based template**, bukan LLM — deterministik dan tidak butuh koneksi eksternal.
- Menyediakan **glosarium bisnis** kuratif untuk istilah kolom/tabel yang umum dipakai.
- Menghasilkan **dokumen knowledge-transfer** (per package/pipeline) yang bisa dibaca orang non-teknis.
- Menyediakan antarmuka web untuk eksplorasi lineage, pencarian, dan impact analysis.

## 4. Non-Tujuan (Non-Goals)

- Tidak menggantikan SSIS sebagai execution engine — tool ini murni untuk dokumentasi & lineage, bukan orkestrasi.
- Tidak mengandalkan LLM sebagai komponen inti (dibuka sebagai enhancement opsional di masa depan, bukan dependency).
- Tidak menangani modifikasi/edit package `.dtsx` — read-only terhadap source.
- Tidak menyimpan kredensial koneksi database dari package yang dipindai.

## 5. Target Pengguna

| Peran | Kebutuhan |
|---|---|
| Data Engineer (individu, termasuk pembuat tool) | Dokumentasi cepat, portfolio project |
| Engineer baru / pengganti | Onboarding tanpa perlu penjelasan A-Z dari orang sebelumnya |
| Business analyst / non-teknis | Memahami alur data laporan tanpa membaca SQL |

## 6. Fitur Utama

### 6.1 Parser `.dtsx`
- Membaca Control Flow: urutan task, precedence constraint (success/failure/completion).
- Membaca Data Flow: source component → transformation → destination component.
- Ekstraksi embedded SQL: `SqlStatementSource` (Execute SQL Task), `CommandText` (OLE DB Source).
- Output terstruktur berupa JSON per package.

### 6.2 SQL Parsing & Lineage Graph
- Ekstraksi table & column reference dari tiap query.
- Bangun graph lineage (table → table, task sebagai edge) hingga level kolom.
- Deteksi impact: kolom/tabel apa saja yang terdampak bila satu node berubah.

### 6.3 Narasi Bisnis (Rule-Based NLG)
- Klasifikasi task berdasarkan pola SQL terdeteksi (Extract / Join / Filter / Aggregate / Load).
- Template kalimat deterministik yang merangkai source table, join key, filter condition, agregasi, dan target table jadi satu narasi bahasa Indonesia.
- Istilah kolom/tabel di narasi terhubung ke glosarium.

### 6.4 Glosarium Bisnis
- Dictionary kuratif (kolom/tabel → definisi bisnis), disunting manual, bukan hasil tebakan otomatis.
- Dipakai untuk substitusi otomatis di narasi & dokumen output.

### 6.5 Generator Dokumen Knowledge-Transfer
- Output per package/pipeline: tujuan bisnis, dependency, jadwal, narasi tiap task, catatan risiko.
- Format ekspor: Markdown / static doc site.

### 6.6 Web UI
- Sidebar navigasi package/task dengan indikator status (OK/warning).
- Canvas lineage graph interaktif (highlight node terpilih & dampak hilir).
- Panel detail: Narasi Bisnis, SQL Asli, Kolom Mapping, Glosarium.
- Pencarian tabel/kolom untuk impact analysis cepat.

*Referensi tampilan: mockup `ssis-lineage-doc-mockup.html` (dibuat 25 Juli 2026).*

## 7. Arsitektur (Ringkas)

```
.dtsx files
    │
    ▼
[Parser XML]  ──►  JSON terstruktur (control flow, data flow, embedded SQL)
    │
    ▼
[SQL Parser]  ──►  table/column reference per query
    │
    ▼
[Lineage Graph Builder]  ──►  graph (node = table/task, edge = alur data)
    │
    ├──► [Rule-based NLG]  ──►  narasi bisnis per task
    │
    ├──► [Glossary Lookup]  ──►  definisi istilah
    │
    ▼
[Doc Generator]  ──►  Markdown / static site
    │
    ▼
[Web UI]  ──►  eksplorasi interaktif, search, impact analysis
```

## 8. Tumpukan Teknologi (Usulan)

| Layer | Tool |
|---|---|
| Parsing XML | `lxml` / `ElementTree` (Python) |
| SQL parsing | `sqlglot` / `sqlparse` |
| Graph | `networkx` |
| Web backend | Python (Flask/FastAPI) |
| Web frontend | HTML/CSS/JS ringan, atau graph viz lib (Cytoscape.js/D3) |
| Doc output | Markdown, opsional static site generator |

## 9. Fase Pengembangan

| Fase | Cakupan | Output |
|---|---|---|
| 1 | Parser `.dtsx` → JSON terstruktur | Skeleton parser, contoh output JSON |
| 2 | SQL parsing + lineage graph builder | Graph table-to-table dari 1 project sample |
| 3 | Rule-based narrative engine + glosarium | Narasi otomatis untuk kasus umum (join/filter/agg/load) |
| 4 | Generator dokumen knowledge-transfer | Output Markdown per package |
| 5 | Web UI | Sesuai mockup: sidebar, canvas graph, panel detail |
| 6 (opsional) | Enhancement LLM untuk polish narasi | Toggle opsional, bukan dependency inti |

## 10. Kriteria Sukses

- Tool dapat memproses seluruh `.dtsx` dalam satu project SSIS nyata tanpa error parsing mayor.
- Narasi bisnis yang dihasilkan cukup jelas dibaca oleh orang yang belum pernah melihat package tersebut.
- Dokumen hasil generate dapat dipakai sebagai referensi onboarding tanpa penjelasan tambahan dari pembuat pipeline.
- Tidak ada dependency ke API eksternal maupun kredensial database yang tersimpan.

## 11. Risiko & Asumsi

| Risiko | Mitigasi |
|---|---|
| SQL dinamis / Script Task yang tidak bisa di-parse statis | Ditandai sebagai "opaque", perlu anotasi manual |
| Variasi struktur `.dtsx` antar versi SSIS | Uji dengan sample package dari berbagai versi lebih awal |
| Glosarium tidak lengkap di awal | Growable, ditambah bertahap seiring pemakaian |
| Narasi rule-based terasa kaku | Diterima sebagai trade-off demi determinisme & keamanan data |

## 12. Lampiran — Contoh Template Narasi

```
{action} data dari {source_table}
[+ digabung dengan {joined_table} berdasarkan {join_key}]
[+ dengan filter {condition}]
[+ lalu diagregasi per {group_columns}]
+ hasilnya dimuat ke {target_table}
```

**Contoh hasil:**
> Mengambil data dari `TRX_HARIAN`, digabung dengan `MASTER_CABANG` berdasarkan `KODE_CAB`, dengan filter `STATUS = 'APPROVED'`, lalu diagregasi per `KODE_CAB`, hasilnya dimuat ke `FCT_KINERJA_HARIAN`.
