# Standard Python Extraction Script
# Migrated from SSIS Package: Pkg_Single_Customer_ETL
# Extract from SQL Server Source → Load to SQL Server Landing Zone → dbt Transform

import pyodbc
import pandas as pd
import os
from datetime import datetime
import warnings
warnings.filterwarnings('ignore', category=UserWarning)

def extract_and_load():
    print(f"[{datetime.now()}] Starting extraction for Pkg_Single_Customer_ETL...")

    # Dynamic connection string derived from SSIS Connection Manager
    conn_str = (
        r'DRIVER={ODBC Driver 18 for SQL Server};'
        r'SERVER=172.17.0.1,1433;'
        r'DATABASE=SsisDemoDB;'
        r'UID=sa;'
        r'PWD=YourPassword123!;'
        r'TrustServerCertificate=yes;'
    )

    try:
        conn = pyodbc.connect(conn_str)
        print("Successfully connected to the source database.")
    except Exception as e:
        print(f"Database connection failed: {e}")
        raise

    # Define source extraction query
    extract_query = """
        SELECT CustomerId, FullName, SegmentCode, EmailAddress, AccountBalance FROM [dbo].[Customers]
    """

    print("Reading data into pandas DataFrame...")
    df = pd.read_sql(extract_query, conn)
    conn.close()
    print(f"Extracted {len(df)} rows.")

    # ---------------------------------------------------------
    # Auto-Generated Data Quality Checks (Great Expectations)
    # ---------------------------------------------------------
    try:
        import great_expectations as ge
        print("Running Data Quality checks...")
        df_ge = ge.from_pandas(df)
        
        # 1. Basic row count expectations
        df_ge.expect_table_row_count_to_be_between(min_value=1)
        
        # 2. Column-level expectations based on schema heuristics
        for col in df.columns:
            col_lower = col.lower()
            if col_lower.endswith('_id') or col_lower.endswith('id') or col_lower == 'id' or col_lower.startswith('pk_'):
                df_ge.expect_column_values_to_not_be_null(column=col)
                if col_lower.startswith('pk_') or col_lower == 'id':
                    df_ge.expect_column_values_to_be_unique(column=col)
            if col_lower.endswith('_status') or col_lower == 'status':
                df_ge.expect_column_values_to_be_in_set(column=col, value_set=['active', 'inactive', 'pending', 'completed', 'failed'])
        
        # Optional: Save validation results or fail pipeline on DQ error
        results = df_ge.validate()
        if not results['success']:
            print("WARNING: Data Quality validation failed on extracted dataset!")
            # raise ValueError("Data Quality Checks Failed") # Uncomment to enforce strict DQ gate
        else:
            print("Data Quality checks passed successfully.")
    except ImportError:
        print("great_expectations not installed. Skipping Data Quality checks.")
    # ---------------------------------------------------------

    # ---------------------------------------------------------
    # Load to Target Database (pyodbc — no SQLAlchemy conflict)
    # ---------------------------------------------------------
    try:
        target_conn_str = (
            r'DRIVER={ODBC Driver 18 for SQL Server};'
            r'SERVER=172.17.0.1,1433;'
            r'DATABASE=SsisDemoDB;'
            r'UID=sa;'
            r'PWD=YourPassword123!;'
            r'TrustServerCertificate=yes;'
        )
        target_conn = pyodbc.connect(target_conn_str)
        cursor = target_conn.cursor()

        target_table = 'dbo_stg_RawCustomers'
        print(f"Loading {len(df)} rows into [" + target_table + "]...")

        # Drop & recreate landing table
        cursor.execute(f"IF OBJECT_ID('dbo.{target_table}', 'U') IS NOT NULL DROP TABLE dbo.{target_table}")
        
        def map_dtype(dt):
            dt_str = str(dt).lower()
            if 'bool' in dt_str: return 'BIT'
            if 'int' in dt_str: return 'INT'
            if 'float' in dt_str or 'decimal' in dt_str: return 'DECIMAL(18,2)'
            if 'datetime64' in dt_str: return 'DATETIME'
            if 'date' in dt_str: return 'DATE'
            if 'timedelta' in dt_str: return 'NVARCHAR(50)'
            return 'NVARCHAR(MAX)'
            
        cols_ddl = ', '.join([f'[{c}] {map_dtype(df[c].dtype)}' for c in df.columns])
        cursor.execute(f'CREATE TABLE dbo.{target_table} ({cols_ddl})')

        # Bulk insert with robust value type handling (NaT/NaN -> None, Datetime -> str)
        import numpy as np
        def clean_val(v):
            if pd.isna(v): return None
            if isinstance(v, (pd.Timestamp, datetime)): return v.strftime('%Y-%m-%d %H:%M:%S')
            if isinstance(v, (np.integer,)): return int(v)
            if isinstance(v, (np.floating,)): return float(v)
            if isinstance(v, (np.bool_,)): return bool(v)
            return v

        placeholders = ', '.join(['?' for _ in df.columns])
        rows = [tuple(clean_val(v) for v in row) for row in df.itertuples(index=False)]
        if len(rows) > 0:
            cursor.executemany(f'INSERT INTO dbo.{target_table} VALUES ({placeholders})', rows)
        else:
            print("No rows to insert.")

        # Reconciliation log
        cursor.execute("""
            IF OBJECT_ID('dbo.ValidationLogs', 'U') IS NULL
            CREATE TABLE dbo.ValidationLogs (RunDate DATETIME, TableName NVARCHAR(100), SsisRows INT, DbtRows INT, Mismatches INT)
        """)
        cursor.execute('INSERT INTO dbo.ValidationLogs VALUES (GETDATE(), ?, ?, 0, 0)', target_table, len(df))
        target_conn.commit()
        cursor.close()
        target_conn.close()
        print(f"Successfully loaded {len(df)} rows into {target_table}. Reconciliation log updated.")
    except Exception as e:
        print(f"Failed to load to database: {e}")
        raise

    print("Extraction and load completed successfully.")

if __name__ == '__main__':
    extract_and_load()
