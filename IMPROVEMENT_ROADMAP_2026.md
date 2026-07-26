# 🚀 SSIS Lineage Hub — Comprehensive Improvement Roadmap & UX Recommendations (2026)

> **Document Version:** 1.0.0  
> **Target System:** SSIS Lineage Hub (Blazor Server .NET 10 & Core Graph Engine)  
> **Author:** Antigravity AI Pair Programmer & Lead Architect  
> **Status:** Strategic Proposal for Continuous Improvement  

---

## 📌 Executive Summary

SSIS Lineage Hub telah bertransformasi dari sekadar parser dokumentasi statis menjadi **Platform Tata Kelola Data & Simulasi Dampak (Governance & Impact Platform)** yang offline-first. 

Dokumen ini memetakan **rekomendasi peningkatan komprehensif** untuk aspek **UI/UX, Fitur Analitis, Performa, dan Integration Engine** agar platform ini memiliki kualitas sekelas produk SaaS enterprise modern.

---

## 🎨 1. User Experience (UX) & UI Aesthetics Improvements

### 1.1 Command Palette (`Ctrl + K` / `Cmd + K`)
- **Masalah Saat Ini:** Pengguna harus menavigasi antar halaman untuk mencari entitas (tabel, kolom, package).
- **Rekomendasi Solusi:** Implementasikan **Global Command Palette Modal** (Spotlight/Raycast style):
  - Tekan `Ctrl + K` dari halaman manapun.
  - Cari tabel (`dbo.MasterPinjaman`), kolom (`OsPokok`), atau package (`Pkg_02`).
  - Langsung lompat ke graph node, simulator, atau data contract terkait.

### 1.2 Cytoscape Minimap & Graph Controls
- **Masalah Saat Ini:** Pada graph berukuran besar (50+ node), pengguna kehilangan konteks orientasi saat melakukan zoom-in.
- **Rekomendasi Solusi:**
  - Tambahkan **MiniMap Navigator** di pojok kanan bawah canvas graph.
  - **Hover Inspection Card**: Saat kursor diarahkan ke node, tampilkan card melayang (*tooltip glassmorphism*) berisi info ringkas (jumlah incoming/outgoing edges, schema, dan risk score).
  - **Pulsing Neon Search Highlight**: Node yang cocok dengan query pencarian akan menyala (*neon glow*) pada canvas.

### 1.3 State Persistence (Auto-Save Workspace)
- **Masalah Saat Ini:** Jika halaman di-refresh, pengguna perlu memilih ulang preset project path dan connection string.
- **Rekomendasi Solusi:**
  - Simpan `projectPath`, `sqlConnectionString`, dan `includeSqlProcedures` ke `localStorage` browser via JS Interop.
  - Begitu aplikasi dibuka, workspace terakhir otomatis dimuat tanpa input ulang.

---

## 🧠 2. Feature & Analytical Enhancements

### 2.1 SSIS Dynamic Expression & Variable Solver
- **Problem Space:** Beberapa package SSIS enterprise tingkat tinggi menggunakan `SqlCommandFromVariable` atau ekspresi dinamis (misal: `@[User::StagingTableName] = "stg.Raw_" + @[User::BatchDate]`).
- **Feature Proposal:**
  - Buat **SSIS Expression Evaluator** ringan di `SsisPackageParser.cs` untuk memecahkan variabel string dinamis sehingga nama tabel akhir dapat terekstrak 100%.

### 2.2 Lineage Time Machine (Historical Snapshot Diffing)
- **Problem Space:** Tim SSIS sering bertanya *"Apa yang berubah di alur ETL ini dibanding bulan lalu?"*.
- **Feature Proposal:**
  - **Snapshot Manager**: Menyimpan snapshot scan lineage format JSON (misal `snapshot-2026-07.json`).
  - **Side-by-Side Timeline Diff**: UI membandingkan 2 snapshot secara berdampingan:
    - 🟢 **Added Mappings** (Glow hijau)
    - 🔴 **Deleted / Broken Mappings** (Merah)
    - 🟡 **Modified Transformations** (Kuning)

### 2.3 Auto-Generated HTML/Markdown Knowledge Wiki (#8)
- **Problem Space:** Dokumentasi ETL sering kedaluwarsa karena ditulis manual.
- **Feature Proposal:**
  - Tombol **"Generate Wiki"** satu kali klik yang menghasilkan static site (HTML/Markdown bundle) lengkap dengan diagram **Mermaid.js** untuk setiap package & tabel.

---

## ⚡ 3. Performance & Architecture Optimization

### 3.1 WebWorker / Background Async Graph Rendering
- **Problem Space:** Render graph Cytoscape dengan >500 node pada thread utama Blazor dapat menyebabkan sedikit lag pada browser.
- **Feature Proposal:**
  - Pindahkan perhitungan kalkulasi posisi layout Cytoscape (`cola` / `dagre` layout engine) ke **WebWorker JS** agar UI tetap responsif 60 FPS.

### 3.2 SQL Procedure Deep Parser (CTE & Dynamic SQL Support)
- **Feature Proposal:**
  - Tingkatkan `SqlProcedureParser.cs` agar mendukung ekspresi SQL kompleks seperti **CTEs (`WITH cte AS (...)`)**, **Window Functions (`OVER (PARTITION BY)`)**, dan **`EXEC sp_executesql`**.

---

## 🛠️ 4. Developer Operations & CI/CD Integration

### 4.1 CI/CD Quality Gate CLI (`ssis-lineage-cli`)
- **Problem Space:** Tim dev ingin mencegah PR / commit yang merusak alur data sebelum di-merge ke branch `main`.
- **Feature Proposal:**
  - Buat perintah CLI executable:
    ```bash
    ssis-lineage validate --project ./sample-ssis-project --contract contract.yaml --risk-threshold 75
    ```
  - Jika ada **Breaking Contract** atau **Risk Score > 75**, CLI mengembalikan exit code `1` untuk menghentikan build pipeline (GitHub Actions / Azure DevOps).

### 4.2 PDF & High-Res SVG Graph Exporter
- **Feature Proposal:**
  - Ekspor diagram lineage interaktif langsung menjadi file **Vector SVG** atau **PDF Landscape A3** untuk keperluan audit arsitektur enterprise.

---

## 📋 Recommended Action Plan & Priority Matrix

| Prioritas | Bidang | Fitur / Peningkatan | Estimasi Impact | Tingkat Kesulitan |
|:---:|---|---|:---:|:---:|
| 1️⃣ | **UX** | Workspace State Persistence (Auto-save form ke LocalStorage) | ⭐⭐⭐⭐⭐ | 🟢 Easy (0.5 hari) |
| 2️⃣ | **UI** | Command Palette Modal (`Ctrl+K`) | ⭐⭐⭐⭐⭐ | 🟡 Medium (1.5 hari) |
| 3️⃣ | **Engine** | Auto Wiki Generator (Export HTML/Markdown + Mermaid) | ⭐⭐⭐⭐⭐ | 🟡 Medium (2 hari) |
| 4️⃣ | **CI/CD** | `ssis-lineage-cli` Quality Gate untuk GitHub Actions | ⭐⭐⭐⭐ | 🟡 Medium (2 hari) |
| 5️⃣ | **Analytics**| Lineage Time Machine (Historical Snapshot Diff) | ⭐⭐⭐⭐⭐ | 🟠 Hard (3 hari) |

---
*Dokumen ini dapat digunakan sebagai landasan diskusi backlog produk dan pengembangan iteratif berikutnya.*
