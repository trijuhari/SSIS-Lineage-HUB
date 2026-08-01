# SSIS Modernization Journey
## From Package-Based ETL to Code-Based Data Engineering

> **Data Engineering Team | 2026**  
> *INTERNAL DOCUMENT — CONFIDENTIAL*

---

## Daftar Isi

1. [Executive Summary](#1-executive-summary)
2. [Apa yang Tidak Berubah](#2-apa-yang-tidak-berubah)
3. [Apa yang Berubah: SSIS ke Modern Stack](#3-apa-yang-berubah-ssis-ke-modern-stack)
4. [Mengapa Meninggalkan SSIS](#4-mengapa-meninggalkan-ssis)
5. [Arsitektur Baru: SQL Server + Modern DE Tools](#5-arsitektur-baru-sql-server--modern-de-tools)
6. [Recommended Banking Stack 2026](#6-recommended-banking-stack-2026)
7. [Struktur Repository](#7-struktur-repository)
8. [Deep Dive: Setiap Komponen Stack](#8-deep-dive-setiap-komponen-stack)
9. [Panduan Transisi untuk Tim](#9-panduan-transisi-untuk-tim)
10. [Pendekatan Migrasi: Incremental, Tidak Big Bang](#10-pendekatan-migrasi-incremental-tidak-big-bang)
11. [Testing Strategy](#11-testing-strategy)
12. [Security & Secrets Management](#12-security--secrets-management)
13. [Observability & Monitoring](#13-observability--monitoring)
14. [CI/CD Pipeline](#14-cicd-pipeline)
15. [FAQ & Pertanyaan Umum dari Tim Legacy](#15-faq--pertanyaan-umum-dari-tim-legacy)
16. [Ringkasan Manfaat](#16-ringkasan-manfaat)
17. [Next Steps](#17-next-steps)

---

## 1. Executive Summary

Dokumen ini menjelaskan inisiatif modernisasi pipeline data di tim Data Engineering, khususnya perpindahan dari pendekatan *package-based ETL* menggunakan SSIS menuju *code-based data engineering* yang lebih modern, maintainable, dan scalable.

> ✅ **SQL Server TETAP digunakan** sebagai Source System, Operational Data Store, dan Data Warehouse  
> ✅ **Tidak ada perubahan** pada database platform yang digunakan oleh tim DBA dan aplikasi  
> ✅ **Yang berubah** adalah cara kita membangun pipeline: dari SSIS menjadi Python + dbt + Airflow  
> ✅ Perubahan ini meningkatkan kualitas kode, testability, dan kecepatan delivery

---

## 2. Apa yang Tidak Berubah

Salah satu hal paling penting untuk dipahami adalah bahwa inisiatif ini **tidak mengubah database platform**. SQL Server tetap menjadi tulang punggung operasional.

| Komponen | Status | Keterangan |
|----------|--------|------------|
| SQL Server (OLTP) | **TETAP** | Source system untuk semua data transaksi |
| SQL Server (DWH) | **TETAP** | Data Warehouse utama tidak berubah |
| SQL Server (Landing) | **TETAP** | Intermediate storage untuk raw data |
| Skema & Tabel Existing | **TETAP** | Tidak ada breaking change pada struktur data |
| Stored Procedures Kritis | **TETAP** | Tidak dihapus, digunakan selektif sesuai kebutuhan |
| Akses DBA | **TETAP** | Tim DBA tetap memiliki full control atas database |

---

## 3. Apa yang Berubah: SSIS ke Modern Stack

Yang berubah adalah **tooling** untuk membangun dan mengoperasikan pipeline data, bukan database platform itu sendiri.

### 3.1 Mapping Tool: Lama vs Baru

| Fungsi | Tool Lama (SSIS Era) | Tool Baru (Modern Stack) | Alasan Utama |
|--------|----------------------|--------------------------|--------------|
| Data Extraction | SSIS Data Source | Python (SQLAlchemy / pyodbc) | Flexible, testable, version-controlled |
| Transformation Logic | SSIS Data Flow, Derived Column, Lookup | dbt (SQL-based models) | Readable, reviewable, auto-documented |
| Execute SQL Task | SSIS Execute SQL Task | dbt Model / Python | Terstandarisasi, dapat di-test |
| Job Scheduling | SQL Server Agent + SSIS Catalog | Apache Airflow | Monitoring terpusat, retry otomatis |
| Ad-hoc Orchestration | Windows Task Scheduler | Airflow DAG | Konsisten, observable |
| Deployment | Manual via SSDT / SSMS | CI/CD Pipeline (GitLab/Azure DevOps) | Otomatis, reproducible |
| Version Control | Tidak standar (.dtsx binary) | Git | Full history, branching, code review |
| Configuration | SSIS Package Config / Environment | YAML / .env / Vault | Terpisah dari kode, secure |

---

## 4. Mengapa Meninggalkan SSIS

Ini bukan keputusan berdasarkan tren semata. SSIS memiliki keterbatasan nyata yang menghambat produktivitas tim DE di lingkungan banking yang terus berkembang.

### 4.1 Tantangan Deployment

> **Problem: SSIS Deployment Sulit dan Berisiko**  
> Setiap deployment membutuhkan akses langsung ke server (SSMS / SSDT), tidak ada automated deployment pipeline yang reliable, koordinasi manual dengan infra team untuk setiap perubahan, rollback memerlukan restore file `.dtsx` secara manual, dan tidak ada staging environment yang mudah dikonfigurasi.

### 4.2 Tantangan Version Control

File SSIS (`.dtsx`) adalah XML binary yang sulit dibaca dan di-diff:

- Git diff pada file `.dtsx` tidak *meaningful* — tidak bisa code review dengan mudah
- Branching dan merging sangat berisiko — sering terjadi konflik yang sulit diselesaikan
- Tidak ada audit trail yang jelas tentang siapa mengubah logic apa
- Rollback ke versi sebelumnya membutuhkan proses manual yang error-prone

### 4.3 Tantangan Testing

SSIS tidak memiliki framework testing yang terstandarisasi:

- Tidak ada built-in unit testing untuk transformation logic
- Data quality check harus dibangun secara manual dan sulit di-maintain
- Regression testing membutuhkan effort manual yang besar
- Tidak ada cara mudah untuk test perubahan sebelum masuk production

### 4.4 Tantangan Reusability & Maintainability

- Logic yang sama sering di-duplicate antar SSIS package
- Sulit untuk membuat shared component yang reusable
- Onboarding engineer baru memerlukan waktu lama karena learning curve GUI yang tidak standar
- Dokumentasi logic transformasi tersebar di dalam GUI, sulit diekstrak

---

## 5. Arsitektur Baru: SQL Server + Modern DE Tools

### 5.1 Flow Utama: OLTP ke Data Mart

```
SQL Server OLTP          ←  Source System (tidak berubah)
        │
        ▼
Python Extraction        ←  pyodbc / SQLAlchemy
        │
        ▼
SQL Server Landing       ←  Raw Layer (tidak berubah, schema baru)
        │
        ▼
dbt Transformation       ←  Staging → Intermediate → Mart
        │
        ▼
SQL Server DWH           ←  Data Warehouse (tidak berubah)
        │
        ▼
Power BI / Reporting     ←  Presentation Layer
```

### 5.2 Penjelasan Setiap Layer

#### Layer 1: SQL Server (Source)

Tidak ada perubahan pada sisi source. Data tetap ada di SQL Server yang dioperasikan oleh tim DBA. Python hanya melakukan `SELECT` untuk membaca data — tidak ada DDL, tidak ada write ke tabel OLTP.

#### Layer 2: Python Extraction

Python menggantikan SSIS sebagai extraction engine. Keunggulan dibanding SSIS Source:

- Mudah ditambahkan validasi dengan Pydantic atau Pandera
- Dapat di-unit test tanpa koneksi database (mocking)
- Mendukung berbagai source: SQL Server, REST API, CSV, Excel, JSON
- Full version control — setiap perubahan logic extraction tercatat di Git

#### Layer 3: SQL Server Landing Zone

Data raw hasil extraction disimpan ke SQL Server di schema khusus (misalnya: `landing` atau `raw`). Layer ini tetap di SQL Server, tidak ada perubahan dari sisi DBA.

#### Layer 4: dbt Transformation

dbt menjalankan SQL transformation langsung di atas SQL Server. Ini menggantikan SSIS Data Flow, Execute SQL Task, dan Stored Procedure chain.

| Aspek | SSIS (Sebelum) | dbt (Sesudah) |
|-------|----------------|---------------|
| Cara Kerja | Drag & drop di GUI Visual Studio | SQL di file `.sql` yang version-controlled |
| Dokumentasi | Tersebar di dalam paket SSIS | Auto-generated dari kode + YAML |
| Testing | Manual, tidak standar | Built-in: `not_null`, `unique`, `accepted_values`, dll |
| Review | Sulit — file `.dtsx` tidak readable | Mudah — plain SQL via Git pull request |
| Lineage | Tidak ada visualisasi otomatis | Auto-generated lineage graph |
| Reusability | Sulit — harus duplicate logic | Mudah — `ref()` dan macro |

#### Layer 5: Airflow Orchestration

Apache Airflow menggantikan kombinasi SQL Server Agent + SSIS Catalog + Windows Task Scheduler. Semua pipeline diatur dalam satu tempat dengan monitoring terpusat.

```
01_extract_customer
        │
        ▼
02_load_to_landing
        │
        ▼
03_dbt_run
        │
        ▼
04_dbt_test
        │
        ▼
05_notify (email / Teams / Slack)
```

Keunggulan Airflow:

- Retry otomatis jika task gagal, dengan notifikasi ke email/Slack/Teams
- Monitoring dashboard terpusat untuk semua pipeline
- Dependency management antar pipeline yang jelas
- DAG as code — full version control via Git
- SLA monitoring bawaan

---

## 6. Recommended Banking Stack 2026

Stack yang direkomendasikan untuk tim DE banking dengan existing SQL Server infrastructure:

| Layer | Tool | Fungsi | Catatan |
|-------|------|--------|---------|
| Source DB | SQL Server | OLTP / Source System | Tidak berubah |
| Extraction | Python (pyodbc, pandas) | Baca data dari source | Ganti SSIS Source |
| Raw Storage | SQL Server (landing schema) | Simpan data mentah | Tidak berubah |
| Transformation | dbt (SQL Server adapter) | Staging + Data Mart | Ganti SSIS Data Flow |
| Data Warehouse | SQL Server | Final analytical layer | Tidak berubah |
| Orchestration | Apache Airflow | Scheduling + Monitoring | Ganti SQL Agent + SSIS |
| Version Control | Git (GitLab / Azure Repos) | Code history + review | Baru |
| CI/CD | GitLab CI / Azure DevOps | Automated deployment | Baru |
| Container | Docker | Environment consistency | Opsional, recommended |
| Data Quality | dbt Tests + Great Expectations | Validasi data otomatis | Ganti manual check |
| Secrets | Azure Key Vault / HashiCorp Vault | Manajemen credential | Security requirement |
| Monitoring | Airflow + Grafana | Observability pipeline | Ganti manual monitoring |

---

## 7. Struktur Repository

Semua kode disimpan dalam satu repository Git dengan struktur yang terstandarisasi:

```
data-platform/
├── dags/                          # Airflow DAG definitions
│   ├── ingestion/
│   │   ├── customer_pipeline.py
│   │   ├── loan_pipeline.py
│   │   └── deposit_pipeline.py
│   └── utils/
│       ├── common_tasks.py
│       └── notifications.py
│
├── ingestion/                     # Python extraction scripts
│   ├── customer/
│   │   ├── extract_customer.py
│   │   └── validate_customer.py
│   ├── loan/
│   └── deposit/
│
├── dbt_project/                   # dbt transformation models
│   ├── models/
│   │   ├── staging/               # Raw to staging layer (1-to-1 dengan source)
│   │   │   ├── stg_customer.sql
│   │   │   ├── stg_loan.sql
│   │   │   └── stg_deposit.sql
│   │   ├── intermediate/          # Business logic & joins
│   │   │   └── int_customer_loan.sql
│   │   └── mart/                  # Data mart / reporting layer
│   │       ├── dim_customer.sql
│   │       └── fct_loan_daily.sql
│   ├── tests/                     # Custom dbt tests (generic & singular)
│   ├── macros/                    # Reusable SQL macros
│   ├── seeds/                     # Reference data (lookup tables)
│   ├── snapshots/                 # SCD Type 2 via dbt snapshot
│   └── dbt_project.yml
│
├── tests/                         # Unit tests untuk ingestion scripts
│   ├── test_extract_customer.py
│   └── conftest.py
│
├── configs/                       # Environment configs (non-secret)
│   ├── dev.yml
│   ├── staging.yml
│   └── prod.yml
│
├── docs/                          # Architecture docs, runbooks
│   ├── architecture.md
│   ├── runbooks/
│   └── onboarding/
│
├── deployment/                    # CI/CD scripts, Docker configs
│   ├── Dockerfile
│   ├── docker-compose.yml
│   └── .gitlab-ci.yml
│
├── .env.example                   # Template environment variables (tanpa secret)
├── requirements.txt
└── README.md
```

---

## 8. Deep Dive: Setiap Komponen Stack

### 8.1 Python Extraction

#### Pattern Dasar: Extract ke Landing

```python
# ingestion/customer/extract_customer.py

import pyodbc
import pandas as pd
from datetime import date
from ingestion.utils.db import get_connection, get_engine
from ingestion.utils.logger import get_logger

logger = get_logger(__name__)

def extract_customer(run_date: date) -> pd.DataFrame:
    """
    Extract customer data dari OLTP untuk tanggal tertentu.
    Tidak ada write ke source — hanya SELECT.
    """
    conn = get_connection(env="source_oltp")
    query = """
        SELECT
            customer_id,
            customer_name,
            customer_type_code,
            registration_date,
            is_active,
            last_modified_date
        FROM dbo.customer
        WHERE CAST(last_modified_date AS DATE) = ?
    """
    logger.info(f"Extracting customer data for {run_date}...")
    df = pd.read_sql(query, conn, params=[run_date])
    logger.info(f"Extracted {len(df):,} rows")
    return df


def load_to_landing(df: pd.DataFrame, run_date: date) -> None:
    """
    Load raw data ke SQL Server landing schema.
    Append dengan metadata run_date untuk auditability.
    """
    engine = get_engine(env="landing")
    df["_run_date"] = run_date
    df["_loaded_at"] = pd.Timestamp.utcnow()

    df.to_sql(
        name="raw_customer",
        con=engine,
        schema="landing",
        if_exists="append",  # gunakan 'replace' hanya di dev
        index=False,
        chunksize=10_000,
    )
    logger.info(f"Loaded {len(df):,} rows to landing.raw_customer")


if __name__ == "__main__":
    from datetime import date
    run_date = date.today()
    df = extract_customer(run_date)
    load_to_landing(df, run_date)
```

#### Pattern: Validasi dengan Pandera

```python
# ingestion/customer/validate_customer.py

import pandera as pa
from pandera import Column, DataFrameSchema, Check

customer_schema = DataFrameSchema({
    "customer_id": Column(int, nullable=False, unique=True),
    "customer_name": Column(str, nullable=False),
    "customer_type_code": Column(str, Check.isin(["IND", "COR", "GOV"])),
    "registration_date": Column(pa.DateTime, nullable=False),
    "is_active": Column(bool, nullable=False),
})

def validate_customer(df):
    return customer_schema.validate(df, lazy=True)
```

#### Pattern: API Integration

```python
# ingestion/utils/api_client.py

import requests
from tenacity import retry, stop_after_attempt, wait_exponential

@retry(stop=stop_after_attempt(3), wait=wait_exponential(min=1, max=10))
def fetch_from_api(url: str, headers: dict, params: dict = None) -> dict:
    """
    Generic API client dengan retry logic.
    Cocok untuk ekstrak data dari core banking API atau third-party.
    """
    response = requests.get(url, headers=headers, params=params, timeout=30)
    response.raise_for_status()
    return response.json()
```

### 8.2 dbt Transformation

#### Staging Layer (1-to-1 dengan source, hanya rename & cast)

```sql
-- models/staging/stg_customer.sql
-- Satu model per source table. Tidak ada join di sini.

WITH source AS (
    SELECT * FROM {{ source('landing', 'raw_customer') }}
),

renamed AS (
    SELECT
        customer_id                                     AS customer_id,
        UPPER(TRIM(customer_name))                      AS customer_name,
        customer_type_code,
        CAST(registration_date AS DATE)                 AS registration_date,
        CAST(is_active AS BIT)                          AS is_active,
        CAST(last_modified_date AS DATETIME2)           AS last_modified_date,
        _run_date,
        _loaded_at
    FROM source
    WHERE customer_id IS NOT NULL  -- basic deduplication guard
)

SELECT * FROM renamed
```

#### Intermediate Layer (business logic, joins)

```sql
-- models/intermediate/int_customer_loan_summary.sql
-- Business logic: join customer dengan agregat loan

WITH customers AS (
    SELECT * FROM {{ ref('stg_customer') }}
),

loans AS (
    SELECT
        customer_id,
        COUNT(loan_id)          AS total_loans,
        SUM(outstanding_amount) AS total_outstanding,
        MAX(loan_date)          AS last_loan_date
    FROM {{ ref('stg_loan') }}
    WHERE loan_status != 'CLOSED'
    GROUP BY customer_id
)

SELECT
    c.customer_id,
    c.customer_name,
    c.customer_type_code,
    COALESCE(l.total_loans, 0)          AS total_active_loans,
    COALESCE(l.total_outstanding, 0)    AS total_outstanding_amount,
    l.last_loan_date
FROM customers c
LEFT JOIN loans l ON c.customer_id = l.customer_id
```

#### Mart Layer (siap konsumsi BI)

```sql
-- models/mart/dim_customer.sql

WITH base AS (
    SELECT * FROM {{ ref('int_customer_loan_summary') }}
)

SELECT
    customer_id,
    customer_name,
    customer_type_code,
    CASE customer_type_code
        WHEN 'IND' THEN 'Individual'
        WHEN 'COR' THEN 'Corporate'
        WHEN 'GOV' THEN 'Government'
        ELSE 'Unknown'
    END AS customer_type_label,
    total_active_loans,
    total_outstanding_amount,
    CASE
        WHEN total_outstanding_amount > 1000000000 THEN 'Premium'
        WHEN total_outstanding_amount > 100000000  THEN 'Regular'
        ELSE 'Basic'
    END AS customer_segment,
    last_loan_date
FROM base
```

#### dbt Schema & Tests (schema.yml)

```yaml
# models/staging/schema.yml

version: 2

sources:
  - name: landing
    database: DWH_LANDING
    schema: landing
    tables:
      - name: raw_customer
        description: "Raw customer data dari OLTP, di-load oleh ingestion script"
        columns:
          - name: customer_id
            description: "Primary key dari tabel customer OLTP"
            tests:
              - not_null
              - unique

models:
  - name: stg_customer
    description: "Staging model untuk customer. Rename & cast saja, no business logic."
    columns:
      - name: customer_id
        tests:
          - not_null
          - unique
      - name: customer_type_code
        tests:
          - accepted_values:
              values: ['IND', 'COR', 'GOV']
      - name: is_active
        tests:
          - not_null
```

#### dbt Macro (reusable logic)

```sql
-- macros/generate_surrogate_key.sql
-- Macro untuk membuat surrogate key yang konsisten

{% macro generate_surrogate_key(column_names) %}
    CONVERT(VARCHAR(64),
        HASHBYTES('SHA2_256',
            CONCAT_WS('|',
                {% for col in column_names %}
                    CAST(COALESCE({{ col }}, '') AS VARCHAR(MAX))
                    {% if not loop.last %},{% endif %}
                {% endfor %}
            )
        ),
        2
    )
{% endmacro %}
```

### 8.3 Airflow DAG

#### DAG Template untuk Banking Pipeline

```python
# dags/ingestion/customer_pipeline.py

from datetime import datetime, timedelta
from airflow import DAG
from airflow.operators.python import PythonOperator
from airflow.operators.bash import BashOperator
from airflow.utils.email import send_email

from ingestion.customer.extract_customer import extract_customer, load_to_landing
from ingestion.customer.validate_customer import validate_customer

DEFAULT_ARGS = {
    "owner": "data-engineering",
    "depends_on_past": False,
    "start_date": datetime(2026, 1, 1),
    "email": ["data-engineering@bank.co.id"],
    "email_on_failure": True,
    "email_on_retry": False,
    "retries": 2,
    "retry_delay": timedelta(minutes=5),
    "execution_timeout": timedelta(hours=2),
}

with DAG(
    dag_id="customer_pipeline",
    default_args=DEFAULT_ARGS,
    schedule_interval="0 2 * * *",  # Run pukul 02:00 setiap hari
    catchup=False,
    tags=["ingestion", "customer", "daily"],
    doc_md="""
    ## Customer Daily Pipeline

    Pipeline ini melakukan:
    1. Extract data customer dari SQL Server OLTP
    2. Validasi schema dan business rules
    3. Load ke SQL Server Landing Zone
    4. Jalankan dbt transformation (staging → mart)
    5. Jalankan dbt test untuk data quality

    **Owner:** Data Engineering Team
    **SLA:** Harus selesai sebelum 06:00
    """,
) as dag:

    def extract_and_validate(**context):
        run_date = context["ds"]  # YYYY-MM-DD dari execution date
        df = extract_customer(run_date)
        validated_df = validate_customer(df)
        load_to_landing(validated_df, run_date)

    task_extract = PythonOperator(
        task_id="extract_customer",
        python_callable=extract_and_validate,
    )

    task_dbt_staging = BashOperator(
        task_id="dbt_staging",
        bash_command="cd /opt/dbt && dbt run --select staging.stg_customer --profiles-dir /opt/dbt/profiles",
    )

    task_dbt_mart = BashOperator(
        task_id="dbt_mart",
        bash_command="cd /opt/dbt && dbt run --select mart.dim_customer --profiles-dir /opt/dbt/profiles",
    )

    task_dbt_test = BashOperator(
        task_id="dbt_test",
        bash_command="cd /opt/dbt && dbt test --select stg_customer dim_customer --profiles-dir /opt/dbt/profiles",
    )

    # Dependency chain
    task_extract >> task_dbt_staging >> task_dbt_mart >> task_dbt_test
```

---

## 9. Panduan Transisi untuk Tim

### 9.1 Mindset Shift

| Konsep di SSIS (Old World) | Padanan di Modern Stack (New World) | Tools |
|----------------------------|--------------------------------------|-------|
| SSIS Package (.dtsx) | Python Module (.py) atau dbt Model (.sql) | Python, dbt |
| SQL Agent Job | Airflow DAG | Apache Airflow |
| Execute SQL Task | dbt Model + run command | dbt |
| Derived Column Transform | SQL expression di dbt model | dbt (SQL) |
| Lookup Transform | SQL JOIN di dbt model | dbt (SQL) |
| Conditional Split | SQL `CASE WHEN` atau `WHERE` filter | dbt (SQL) |
| SSIS Package Config | YAML config + environment variables | YAML, .env, Vault |
| SSIS Deployment (manual) | CI/CD Pipeline (automated) | GitLab CI / Azure DevOps |
| Manual Testing | Automated Tests (dbt test, pytest) | dbt, pytest |
| Drag & Drop GUI | Code-based (SQL + Python) | VS Code, Git |
| SSIS Log | Airflow task logs + Grafana dashboard | Airflow, Grafana |

### 9.2 Learning Path untuk Engineer

Berikut urutan pembelajaran yang direkomendasikan bagi engineer yang baru migrasi dari SSIS:

**Minggu 1-2: Git & Python Basics**
- Git workflow: branch, commit, pull request
- Python dasar untuk data: pandas, pyodbc, sqlalchemy
- Membuat virtual environment, requirements.txt

**Minggu 3-4: dbt Fundamentals**
- dbt project structure
- Source, model, ref(), test
- Menjalankan `dbt run`, `dbt test`, `dbt docs generate`

**Minggu 5-6: Airflow Basics**
- DAG structure dan concepts
- Operator types: PythonOperator, BashOperator
- Monitoring via Airflow UI

**Minggu 7-8: Praktik Migrasi**
- Pilih 1 SSIS package sederhana
- Convert ke Python + dbt + Airflow
- Code review bersama senior DE

---

## 10. Pendekatan Migrasi: Incremental, Tidak Big Bang

Migrasi dari SSIS ke modern stack **tidak dilakukan sekaligus**. Pendekatan yang direkomendasikan adalah incremental migration:

| Fase | Scope | Durasi Est. | Output |
|------|-------|-------------|--------|
| Fase 1: Foundation | Setup Git repo, dbt project, Airflow instance (dev) | 2-4 minggu | Environment siap |
| Fase 2: Pilot | Migrasi 1-2 pipeline non-kritis sebagai proof of concept | 4-6 minggu | Template & pattern |
| Fase 3: Expansion | Migrasi pipeline batch lainnya secara bertahap | 3-6 bulan | 80% pipeline modern |
| Fase 4: Optimization | Testing, monitoring, dokumentasi lengkap | Ongoing | Full modern stack |

> **Prinsip Migrasi:**
> - SSIS pipeline yang sudah berjalan stabil **TIDAK dihentikan** sebelum pengganti siap dan sudah di-test
> - Parallel running: jalankan SSIS dan modern stack bersamaan untuk validasi output
> - Tim DBA tetap dilibatkan untuk review perubahan yang menyentuh database layer
> - Setiap pipeline yang dimigrasikan harus memiliki automated test sebelum go-live

### 10.1 Kriteria Pipeline untuk Pilot (Fase 2)

Pipeline yang ideal sebagai pilot adalah yang:

- Bukan pipeline kritis (downtime tidak langsung impact ke operasional)
- Sering bermasalah atau memerlukan perubahan logic yang sering
- Transformasinya relatif sederhana (tidak terlalu banyak lookup dan conditional split)
- Tim yang mengelola pipeline ini bersedia jadi early adopter

---

## 11. Testing Strategy

Salah satu kelemahan terbesar SSIS adalah tidak adanya standar testing. Modern stack memiliki testing di setiap layer.

### 11.1 Lapisan Testing

```
Unit Test (pytest)       ← Test logic Python tanpa database
        │
        ▼
Integration Test         ← Test koneksi ke database dev
        │
        ▼
dbt Test                 ← Data quality di setiap model
        │
        ▼
Reconciliation Test      ← Output modern stack vs SSIS (selama parallel run)
```

### 11.2 Unit Test untuk Python Extraction

```python
# tests/test_extract_customer.py

import pytest
import pandas as pd
from unittest.mock import MagicMock, patch
from datetime import date

from ingestion.customer.extract_customer import extract_customer
from ingestion.customer.validate_customer import validate_customer

@pytest.fixture
def sample_customer_df():
    return pd.DataFrame({
        "customer_id": [1, 2, 3],
        "customer_name": ["Budi Santoso", "Siti Rahayu", "Agus Purnomo"],
        "customer_type_code": ["IND", "COR", "IND"],
        "registration_date": pd.to_datetime(["2020-01-01", "2019-05-10", "2021-03-15"]),
        "is_active": [True, True, False],
        "last_modified_date": pd.to_datetime(["2026-06-01"] * 3),
    })

def test_validate_customer_passes_valid_data(sample_customer_df):
    result = validate_customer(sample_customer_df)
    assert len(result) == 3

def test_validate_customer_fails_on_null_id(sample_customer_df):
    import pandera.errors
    sample_customer_df.loc[0, "customer_id"] = None
    with pytest.raises(pandera.errors.SchemaErrors):
        validate_customer(sample_customer_df)

def test_validate_customer_fails_on_invalid_type_code(sample_customer_df):
    import pandera.errors
    sample_customer_df.loc[0, "customer_type_code"] = "INVALID"
    with pytest.raises(pandera.errors.SchemaErrors):
        validate_customer(sample_customer_df)

@patch("ingestion.customer.extract_customer.get_connection")
@patch("ingestion.customer.extract_customer.pd.read_sql")
def test_extract_customer_calls_correct_query(mock_read_sql, mock_conn, sample_customer_df):
    mock_read_sql.return_value = sample_customer_df
    result = extract_customer(date(2026, 6, 1))
    assert len(result) == 3
    mock_read_sql.assert_called_once()
```

### 11.3 dbt Singular Test (Custom Test)

```sql
-- tests/assert_no_duplicate_customer_per_run_date.sql
-- Test: tidak boleh ada customer_id duplikat untuk run_date yang sama

SELECT
    customer_id,
    _run_date,
    COUNT(*) AS cnt
FROM {{ ref('stg_customer') }}
GROUP BY customer_id, _run_date
HAVING COUNT(*) > 1
```

Jika query ini mengembalikan baris, dbt test akan FAIL — sehingga pipeline tidak lanjut ke tahap berikutnya.

### 11.4 Reconciliation Test (Parallel Run)

Selama fase parallel run (SSIS dan modern stack berjalan bersamaan), lakukan reconciliation:

```python
# tests/reconciliation/reconcile_customer.py

import pandas as pd
from ingestion.utils.db import get_connection

def reconcile_customer(run_date: str) -> dict:
    """
    Bandingkan output SSIS (existing DWH) dengan output modern stack (landing).
    Return dict dengan summary perbedaan.
    """
    conn = get_connection(env="dwh")

    # Data dari SSIS path (existing)
    df_ssis = pd.read_sql(f"""
        SELECT customer_id, customer_name, is_active
        FROM dbo.dim_customer_ssis
        WHERE load_date = '{run_date}'
    """, conn)

    # Data dari modern stack path
    df_modern = pd.read_sql(f"""
        SELECT customer_id, customer_name, is_active
        FROM mart.dim_customer
        WHERE _run_date = '{run_date}'
    """, conn)

    # Merge untuk comparison
    merged = df_ssis.merge(df_modern, on="customer_id", suffixes=("_ssis", "_modern"), how="outer", indicator=True)

    result = {
        "total_ssis": len(df_ssis),
        "total_modern": len(df_modern),
        "only_in_ssis": len(merged[merged["_merge"] == "left_only"]),
        "only_in_modern": len(merged[merged["_merge"] == "right_only"]),
        "mismatch_name": len(merged[merged["customer_name_ssis"] != merged["customer_name_modern"]]),
    }
    return result
```

---

## 12. Security & Secrets Management

Di lingkungan banking, secrets management adalah non-negotiable.

### 12.1 Prinsip

- **Tidak ada hardcoded credential** di kode — apapun yang ada di Git harus aman untuk dilihat semua anggota tim
- Connection string, API key, password database — semua melalui Vault atau Key Vault
- `.env` file hanya boleh ada di local dev dan **tidak di-commit ke Git**

### 12.2 Implementasi dengan Azure Key Vault

```python
# ingestion/utils/secrets.py

from azure.identity import DefaultAzureCredential
from azure.keyvault.secrets import SecretClient

_client = None

def get_secret(secret_name: str, vault_url: str) -> str:
    """
    Ambil secret dari Azure Key Vault.
    Menggunakan DefaultAzureCredential — works di local (via az login)
    maupun di server (via Managed Identity).
    """
    global _client
    if _client is None:
        credential = DefaultAzureCredential()
        _client = SecretClient(vault_url=vault_url, credential=credential)

    secret = _client.get_secret(secret_name)
    return secret.value


def get_connection_string(env: str = "prod") -> str:
    vault_url = f"https://bank-de-vault-{env}.vault.azure.net/"
    server = get_secret("sqlserver-host", vault_url)
    database = get_secret("sqlserver-database", vault_url)
    username = get_secret("sqlserver-username", vault_url)
    password = get_secret("sqlserver-password", vault_url)
    return f"mssql+pyodbc://{username}:{password}@{server}/{database}?driver=ODBC+Driver+17+for+SQL+Server"
```

### 12.3 .env.example (template, boleh di-commit)

```bash
# .env.example — JANGAN ISI DENGAN NILAI ASLI
# Copy ke .env dan isi dengan nilai yang sesuai (jangan commit .env)

VAULT_URL=https://your-vault.vault.azure.net/
AIRFLOW_FERNET_KEY=
AIRFLOW_DB_CONN=
DBT_PROFILES_DIR=/opt/dbt/profiles
ENVIRONMENT=dev  # dev | staging | prod
```

---

## 13. Observability & Monitoring

### 13.1 Monitoring di Airflow

Airflow menyediakan monitoring bawaan:

- **Task success/failure rate** per DAG
- **Task duration** untuk deteksi regresi performa
- **SLA miss alerts** — notifikasi jika pipeline tidak selesai tepat waktu
- **Log terpusat** — semua log task tersimpan dan searchable

### 13.2 Custom Alerting

```python
# dags/utils/notifications.py

from airflow.utils.email import send_email
from datetime import datetime

def on_failure_callback(context):
    """Kirim notifikasi ke Teams / email saat task gagal."""
    dag_id = context["dag"].dag_id
    task_id = context["task_instance"].task_id
    execution_date = context["execution_date"]
    log_url = context["task_instance"].log_url

    subject = f"[ALERT] Pipeline Gagal: {dag_id} > {task_id}"
    body = f"""
    <h3>Pipeline Failure Alert</h3>
    <p><b>DAG:</b> {dag_id}</p>
    <p><b>Task:</b> {task_id}</p>
    <p><b>Execution Date:</b> {execution_date}</p>
    <p><b>Log:</b> <a href="{log_url}">Lihat Log</a></p>
    """
    send_email(to=["data-engineering@bank.co.id"], subject=subject, html_content=body)
```

### 13.3 dbt Artifacts untuk Dokumentasi

```bash
# Generate dokumentasi dbt (auto-generated lineage graph, model descriptions)
dbt docs generate
dbt docs serve --port 8080
```

Dokumentasi ini dapat di-host secara internal dan di-akses oleh semua tim (DE, DBA, BI, Business).

---

## 14. CI/CD Pipeline

### 14.1 GitLab CI Example

```yaml
# deployment/.gitlab-ci.yml

stages:
  - lint
  - test
  - dbt-check
  - deploy

variables:
  PYTHON_VERSION: "3.11"
  DBT_PROFILES_DIR: "/opt/dbt/profiles"

lint:
  stage: lint
  script:
    - pip install ruff
    - ruff check ingestion/ dags/ tests/
  only:
    - merge_requests
    - main

unit-test:
  stage: test
  script:
    - pip install -r requirements.txt
    - pytest tests/ -v --tb=short --cov=ingestion --cov-report=xml
  coverage: '/TOTAL.*\s+(\d+%)$/'
  artifacts:
    reports:
      coverage_report:
        coverage_format: cobertura
        path: coverage.xml
  only:
    - merge_requests
    - main

dbt-compile:
  stage: dbt-check
  script:
    - dbt deps
    - dbt compile --profiles-dir $DBT_PROFILES_DIR
  only:
    - merge_requests

deploy-staging:
  stage: deploy
  script:
    - dbt run --target staging --profiles-dir $DBT_PROFILES_DIR
    - dbt test --target staging --profiles-dir $DBT_PROFILES_DIR
  environment:
    name: staging
  only:
    - main

deploy-prod:
  stage: deploy
  script:
    - dbt run --target prod --profiles-dir $DBT_PROFILES_DIR
    - dbt test --target prod --profiles-dir $DBT_PROFILES_DIR
  environment:
    name: production
  when: manual  # Require manual approval untuk prod deployment
  only:
    - main
```

---

## 15. FAQ & Pertanyaan Umum dari Tim Legacy

**Q: Apakah SQL Server kita akan diganti?**  
A: Tidak. SQL Server tetap digunakan sebagai source system, landing zone, dan data warehouse. Tidak ada perubahan database platform.

**Q: Apakah stored procedure yang sudah ada akan dihapus?**  
A: Tidak langsung. SP yang masih digunakan tetap jalan. Yang kami ganti adalah cara membangun *pipeline baru* — bukan memaksa semua legacy kode untuk langsung dikonversi.

**Q: Tim DBA apakah masih perlu terlibat?**  
A: Ya, dan keterlibatan mereka tetap penting. DBA tetap menjadi pemilik database, schema, dan permission. Kami hanya meminta akses *read* dari sisi source, dan *write* ke schema landing/DWH yang sudah disepakati.

**Q: Apa yang terjadi jika pipeline Python gagal? Apakah lebih sulit di-debug daripada SSIS?**  
A: Justru lebih mudah. Airflow menyimpan log setiap task, error message lebih eksplisit, dan bisa langsung di-rerun dari titik kegagalan tanpa harus restart seluruh package. Di SSIS, sering kali error message tidak deskriptif dan sulit di-trace.

**Q: Apakah Airflow bisa replace SQL Server Agent sepenuhnya?**  
A: Untuk pipeline data engineering, ya. SQL Server Agent tetap bisa digunakan untuk maintenance task seperti index rebuild, backup, dll — itu domain DBA dan tidak kami sentuh.

**Q: Seberapa lama proses migrasi?**  
A: Estimasi 6-9 bulan untuk 80% pipeline, tergantung kompleksitas dan jumlah pipeline. Migrasi bersifat incremental — SSIS tidak langsung dimatikan.

**Q: Apakah engineer perlu bisa Python?**  
A: Untuk extraction layer, ya. Tapi untuk transformation (dbt), yang dibutuhkan adalah SQL — yang sebagian besar engineer DE sudah familiar. Python yang digunakan pun relatif sederhana (bukan ML atau data science).

---

## 16. Ringkasan Manfaat

| Aspek | Sebelum (SSIS) | Sesudah (Modern Stack) | Dampak |
|-------|---------------|------------------------|--------|
| Deployment | Manual, butuh server access | Automated via CI/CD | Lebih cepat, lebih aman |
| Code Review | Tidak praktis (.dtsx binary) | Standard via Git MR/PR | Kualitas kode meningkat |
| Testing | Manual, tidak konsisten | Automated, standar | Bug lebih cepat ketahuan |
| Monitoring | Tersebar di SQL Agent + SSIS | Terpusat di Airflow | Visibility lebih baik |
| Onboarding | Butuh SSIS expertise khusus | SQL + Python standar industri | Lebih mudah rekrut & onboard |
| Dokumentasi | Manual, sering outdated | Auto-generated dari kode | Selalu up-to-date |
| Rollback | Manual, berisiko | Git revert otomatis | Recovery lebih cepat |
| Skalabilitas | Terbatas pada Windows Server | Cloud-ready, container-ready | Future-proof |
| Secrets Management | Hardcoded / SSIS Config | Vault / Key Vault | Security compliant |
| Auditability | Terbatas | Full Git history + Airflow logs | Audit-ready |

---

## 17. Next Steps

1. **Kick-off meeting** dengan tim DE, DBA, dan stakeholder terkait — alignment atas scope dan timeline
2. **Setup environment**: Git repository, dbt project skeleton, Airflow dev instance
3. **Pilih pilot pipeline**: kandidat ideal adalah pipeline yang sering bermasalah atau butuh perubahan
4. **Develop, test, dan validasi** pilot pipeline secara parallel dengan SSIS existing
5. **Review hasil pilot**, dokumentasikan learnings, dan planning untuk Fase 3 (Expansion)
6. **Establish coding standards**: PR template, linting rules, naming conventions untuk dbt model dan DAG

---

*Dokumen ini bersifat living document dan akan diupdate seiring berjalannya proyek modernisasi.*

**Internal Document — Confidential | Data Engineering Team | 2026**
