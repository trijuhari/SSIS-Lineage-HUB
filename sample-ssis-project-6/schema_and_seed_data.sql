-- ============================================================
-- Sample Database Schema & Seed Data
-- For testing ssis-lineage-tool against:
--   Pkg_00_Master_ETL_Orchestration.dtsx
--   Pkg_01_Extract_EnterpriseHR_Payroll.dtsx
--   Pkg_02_Transform_FinancialLedger.dtsx
--   Pkg_03_Load_FactEnterprisePerformance.dtsx
-- ============================================================

-- ============================================================
-- 1. SOURCE: HR_PayrollSystem
-- ============================================================
CREATE DATABASE HR_PayrollSystem;
GO
USE HR_PayrollSystem;
GO

CREATE TABLE dbo.Employee (
    EmployeeID       INT PRIMARY KEY,
    FullName         VARCHAR(150) NOT NULL,
    DepartmentCode   VARCHAR(20)  NOT NULL,
    HireDate         DATE         NOT NULL,
    EmploymentStatus VARCHAR(20)  NOT NULL   -- ACTIVE, INACTIVE, TERMINATED
);
GO

CREATE TABLE dbo.PayrollTransaction (
    PayrollTxnID       INT IDENTITY PRIMARY KEY,
    EmployeeID          INT NOT NULL REFERENCES dbo.Employee(EmployeeID),
    GrossPay             DECIMAL(18,2) NOT NULL,
    TaxDeduction          DECIMAL(18,2) NOT NULL,
    InsuranceDeduction    DECIMAL(18,2) NOT NULL,
    PayPeriod             CHAR(6) NOT NULL   -- format yyyyMM
);
GO

INSERT INTO dbo.Employee (EmployeeID, FullName, DepartmentCode, HireDate, EmploymentStatus) VALUES
(1001, 'Andi Wijaya',    'BR-JKT-01', '2019-03-01', 'ACTIVE'),
(1002, 'Siti Rahmawati',  'BR-JKT-01', '2024-11-15', 'ACTIVE'),
(1003, 'Budi Santoso',    'BR-SBY-02', '2018-07-20', 'ACTIVE'),
(1004, 'Dewi Lestari',    'BR-BDG-03', '2015-01-10', 'INACTIVE'),
(1005, 'Rian Pratama',    'BR-SBY-02', '2021-09-05', 'ACTIVE');
GO

INSERT INTO dbo.PayrollTransaction (EmployeeID, GrossPay, TaxDeduction, InsuranceDeduction, PayPeriod) VALUES
(1001, 15000000, 750000, 300000, FORMAT(GETDATE(), 'yyyyMM')),
(1002, 8000000,  200000, 150000, FORMAT(GETDATE(), 'yyyyMM')),
(1003, 12000000, 600000, 250000, FORMAT(GETDATE(), 'yyyyMM')),
(1004, 10000000, 500000, 200000, FORMAT(GETDATE(), 'yyyyMM')),
(1005, 9000000,  450000, 180000, FORMAT(GETDATE(), 'yyyyMM'));
GO


-- ============================================================
-- 2. SOURCE: CoreBankingLedger
-- ============================================================
CREATE DATABASE CoreBankingLedger;
GO
USE CoreBankingLedger;
GO

CREATE TABLE dbo.LedgerTransaction (
    TransactionID     INT IDENTITY PRIMARY KEY,
    BranchCode        VARCHAR(20)    NOT NULL,
    AccountNumber     VARCHAR(30)    NOT NULL,
    TransactionType   VARCHAR(10)    NOT NULL,   -- DEBIT, CREDIT
    Amount            DECIMAL(18,2)  NOT NULL,
    CurrencyCode      CHAR(3)        NOT NULL,   -- IDR, USD
    TransactionDate   DATETIME       NOT NULL
);
GO

INSERT INTO dbo.LedgerTransaction (BranchCode, AccountNumber, TransactionType, Amount, CurrencyCode, TransactionDate) VALUES
('BR-JKT-01', '1000000001', 'CREDIT', 750000000, 'IDR', GETDATE()),
('BR-JKT-01', '1000000002', 'DEBIT',  25000,      'USD', GETDATE()),
('BR-SBY-02', '1000000003', 'DEBIT',  1200000,    'IDR', GETDATE()),
('BR-SBY-02', '1000000004', 'CREDIT', 600000000,  'IDR', DATEADD(HOUR, -20, GETDATE())),
('BR-BDG-03', '1000000005', 'CREDIT', 3000000,    'IDR', DATEADD(HOUR, -5, GETDATE()));
GO


-- ============================================================
-- 3. TARGET: EnterpriseDataWarehouse
-- ============================================================
CREATE DATABASE EnterpriseDataWarehouse;
GO
USE EnterpriseDataWarehouse;
GO

CREATE SCHEMA stg;
GO
CREATE SCHEMA dim;
GO

-- Dimension tables --------------------------------------------------
CREATE TABLE dim.Branch (
    BranchKey    INT IDENTITY PRIMARY KEY,
    BranchCode   VARCHAR(20) NOT NULL UNIQUE,
    BranchName   VARCHAR(100) NOT NULL,
    RegionCode   VARCHAR(20) NOT NULL
);
GO

CREATE TABLE dim.Date (
    DateKey   INT PRIMARY KEY,     -- yyyymmdd
    FullDate  DATE NOT NULL UNIQUE
);
GO

INSERT INTO dim.Branch (BranchCode, BranchName, RegionCode) VALUES
('BR-JKT-01', 'Jakarta Sudirman',   'REG-WEST'),
('BR-SBY-02', 'Surabaya Darmo',     'REG-EAST'),
('BR-BDG-03', 'Bandung Dago',       'REG-WEST');
GO

INSERT INTO dim.Date (DateKey, FullDate)
SELECT CONVERT(INT, FORMAT(d, 'yyyyMMdd')), d
FROM (SELECT CAST(DATEADD(DAY, -n, GETDATE()) AS DATE) AS d
      FROM (SELECT TOP (7) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
            FROM sys.all_objects) x) y;
GO

-- Staging tables (loaded by Pkg_01 / Pkg_02) -------------------------
CREATE TABLE stg.EmployeePayroll (
    EmployeeID          INT,
    FullName            VARCHAR(150),
    DepartmentCode      VARCHAR(20),
    HireDate            DATE,
    EmploymentStatus    VARCHAR(20),
    GrossPay            DECIMAL(18,2),
    TaxDeduction        DECIMAL(18,2),
    InsuranceDeduction  DECIMAL(18,2),
    PayPeriod           CHAR(6),
    NetPay              DECIMAL(18,2),
    TenureYears         INT
);
GO

CREATE TABLE stg.FinancialLedger (
    BranchCode           VARCHAR(20),
    TransactionCategory  VARCHAR(30),
    TransactionDate      DATETIME,
    TotalAmountIDR        DECIMAL(18,2),
    TransactionCount      INT
);
GO

-- Fact table (loaded by Pkg_03) ---------------------------------------
CREATE TABLE dbo.Fact_EnterprisePerformance (
    FactID                  INT IDENTITY PRIMARY KEY,
    BranchKey                INT REFERENCES dim.Branch(BranchKey),
    DateKey                   INT NULL REFERENCES dim.Date(DateKey),
    DepartmentCode             VARCHAR(20),
    EmployeeID                  INT,
    NetPay                        DECIMAL(18,2),
    TotalAmountIDR                DECIMAL(18,2),
    TransactionCount                INT,
    BranchPerformanceScore           DECIMAL(18,4),
    LoadDate                          DATETIME DEFAULT GETDATE()
);
GO

-- Orchestration / audit tables (used by Pkg_00 master & Pkg_01 log task) ----
CREATE TABLE dbo.ETL_AuditLog (
    BatchId          INT PRIMARY KEY,
    ExecutionStatus  VARCHAR(50),
    StartTime        DATETIME,
    EndTime          DATETIME NULL
);
GO

CREATE TABLE dbo.ETL_RowCountLog (
    LogID        INT IDENTITY PRIMARY KEY,
    BatchId      INT,
    PackageName  VARCHAR(200),
    TableName    VARCHAR(200),
    [RowCount]   INT,
    LogTime      DATETIME
);
GO
