from datetime import datetime, timedelta
from airflow import DAG
from airflow.operators.empty import EmptyOperator
from airflow.operators.bash import BashOperator
from airflow.providers.common.sql.operators.sql import SQLExecuteQueryOperator
from airflow.operators.trigger_dagrun import TriggerDagRunOperator
from airflow.operators.python import PythonOperator
from airflow.sensors.filesystem import FileSensor

def on_task_failure_callback(context):
    ti = context.get('task_instance')
    print(f'[SELF-HEALING ALERT] Task {ti.task_id} in DAG {ti.dag_id} failed. Initiating retry protocol.')

default_args = {
    'owner': 'data_engineering',
    'depends_on_past': False,
    'email_on_failure': False,
    'email_on_retry': False,
    'retries': 3,
    'retry_delay': timedelta(seconds=15),
    'retry_exponential_backoff': True,
    'max_retry_delay': timedelta(minutes=5),
    'on_failure_callback': on_task_failure_callback,
}

with DAG(
    dag_id='dag_single_customer_etl',
    default_args=default_args,
    description='Auto-converted from SSIS Package Pkg_Single_Customer_ETL',
    schedule=None,
    start_date=datetime(2026, 1, 1),
    catchup=False,
    tags=['ssis_migration', 'self_healing'],
) as dag:

    start_pipeline = EmptyOperator(task_id='start_pipeline')
    end_pipeline = EmptyOperator(task_id='end_pipeline', trigger_rule='all_done')

    est_clean_and_prep_staging = SQLExecuteQueryOperator(
        task_id='est_clean_and_prep_staging',
        conn_id='sql_default',
        sql=' -- ── Seed: source table dbo.Customers ────────────────────────────────── IF OBJECT_ID(\'dbo.Customers\', \'U\') IS NULL BEGIN     CREATE TABLE dbo.Customers (         CustomerId     INT           NOT NULL PRIMARY KEY,         FullName       NVARCHAR(100) NOT NULL,         SegmentCode    NVARCHAR(20)  NOT NULL,         EmailAddress   NVARCHAR(100) NULL,         AccountBalance DECIMAL(18,2) NOT NULL DEFAULT 0.00     ); END;  -- Insert seed rows only if the table is empty (idempotent) IF NOT EXISTS (SELECT 1 FROM dbo.Customers) BEGIN     INSERT INTO dbo.Customers (CustomerId, FullName, SegmentCode, EmailAddress, AccountBalance)     VALUES         (1, \'John Doe\',     \'SEG_RETAIL\', \'john.doe@example.com\',    15000.00),         (2, \'Jane Smith\',   \'SEG_CORP\',   \'jane.smith@example.com\',  85000.00),         (3, \'Bob Johnson\',  \'SEG_RETAIL\', \'bob.j@example.com\',        3200.50),         (4, \'Alice Tan\',    \'SEG_WEALTH\', \'alice.tan@example.com\',  250000.00),         (5, \'Carlos Reyes\', \'SEG_CORP\',   \'c.reyes@example.com\',     47500.00); END;  -- ── Seed: lookup table dbo.CustomerSegments ──────────────────────────── IF OBJECT_ID(\'dbo.CustomerSegments\', \'U\') IS NULL BEGIN     CREATE TABLE dbo.CustomerSegments (         SegmentCode  NVARCHAR(20)  NOT NULL PRIMARY KEY,         SegmentName  NVARCHAR(50)  NOT NULL,         SegmentTier  NVARCHAR(20)  NOT NULL DEFAULT \'Standard\'     ); END;  IF NOT EXISTS (SELECT 1 FROM dbo.CustomerSegments) BEGIN     INSERT INTO dbo.CustomerSegments (SegmentCode, SegmentName, SegmentTier)     VALUES         (\'SEG_RETAIL\', \'Retail Banking\',  \'Standard\'),         (\'SEG_CORP\',   \'Corporate Banking\', \'Premium\'),         (\'SEG_WEALTH\', \'Wealth Management\', \'Elite\'); END;  -- ── Landing / staging table dbo.stg_RawCustomers ────────────────────── -- Drop & recreate for a clean slate each run (idempotent) IF OBJECT_ID(\'dbo.stg_RawCustomers\', \'U\') IS NOT NULL     DROP TABLE dbo.stg_RawCustomers;  CREATE TABLE dbo.stg_RawCustomers (     CustomerId       INT           NULL,     FullName         NVARCHAR(100) NULL,     CustomerSegment  NVARCHAR(50)  NULL,     EmailAddress     NVARCHAR(100) NULL,     AccountBalance   DECIMAL(18,2) NULL,     LoadedAt         DATETIME      NOT NULL DEFAULT GETDATE() ); ',
    )

    dft_extract_and_load_customers_extract = BashOperator(
        task_id='dft_extract_and_load_customers_extract_python',
        bash_command='python /opt/airflow/dags/scripts/extract_single_customer_etl.py',
    )

    dft_extract_and_load_customers_dbt = BashOperator(
        task_id='dft_extract_and_load_customers_transform_dbt',
        bash_command='cd /opt/airflow/dags/dbt_project && dbt run --no-partial-parse --profiles-dir . --select stg_single_customer_etl',
    )

    dft_extract_and_load_customers_extract >> dft_extract_and_load_customers_dbt


    # Set up task dependencies (follows original SSIS PrecedenceConstraint order)
    start_pipeline >> est_clean_and_prep_staging
    est_clean_and_prep_staging >> dft_extract_and_load_customers_extract
    dft_extract_and_load_customers_dbt >> end_pipeline
