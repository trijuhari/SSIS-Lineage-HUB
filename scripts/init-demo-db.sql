-- ==========================================================
-- SSIS Lineage Hub — Sample Demo Database Initializer
-- ==========================================================

IF NOT EXISTS (SELECT * FROM sys.databases WHERE name = 'SsisDemoDB')
BEGIN
    CREATE DATABASE SsisDemoDB;
END
GO

USE SsisDemoDB;
GO

-- ── Schemas ───────────────────────────────────────────────
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'stg') EXEC('CREATE SCHEMA stg');
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'dw')  EXEC('CREATE SCHEMA dw');
GO

-- ── Tables ────────────────────────────────────────────────
IF OBJECT_ID('stg.RawCustomers', 'U') IS NOT NULL DROP TABLE stg.RawCustomers;
CREATE TABLE stg.RawCustomers (
    CustomerId INT PRIMARY KEY,
    FullName VARCHAR(100),
    EmailAddress VARCHAR(150),
    CreatedDate DATETIME DEFAULT GETDATE()
);

IF OBJECT_ID('dbo.MasterPinjaman', 'U') IS NOT NULL DROP TABLE dbo.MasterPinjaman;
CREATE TABLE dbo.MasterPinjaman (
    IdPinjaman INT PRIMARY KEY,
    IdAnggota INT NOT NULL,
    OsPokok DECIMAL(18,2) NOT NULL,
    JumlahPinjaman DECIMAL(18,2) NOT NULL,
    StatusPinjaman VARCHAR(20) DEFAULT 'ACTIVE'
);

IF OBJECT_ID('dw.DimAnggota', 'U') IS NOT NULL DROP TABLE dw.DimAnggota;
CREATE TABLE dw.DimAnggota (
    AnggotaKey INT IDENTITY(1,1) PRIMARY KEY,
    IdAnggota INT NOT NULL,
    NamaAnggota VARCHAR(100),
    Email VARCHAR(150),
    IsActive BIT DEFAULT 1
);

IF OBJECT_ID('dw.FactSimpanan', 'U') IS NOT NULL DROP TABLE dw.FactSimpanan;
CREATE TABLE dw.FactSimpanan (
    SimpananKey INT IDENTITY(1,1) PRIMARY KEY,
    AnggotaKey INT NOT NULL,
    JumlahSimpanan DECIMAL(18,2) NOT NULL,
    TanggalTransaksi DATE NOT NULL
);
GO

-- ── Sample Stored Procedure for Procedure Lineage Enrichment ──
IF OBJECT_ID('dbo.sp_ProcessLoanLineage', 'P') IS NOT NULL DROP PROCEDURE dbo.sp_ProcessLoanLineage;
GO
CREATE PROCEDURE dbo.sp_ProcessLoanLineage
AS
BEGIN
    SET NOCOUNT ON;

    -- Procedure flow: stg.RawCustomers + dbo.MasterPinjaman -> dw.DimAnggota & dw.FactSimpanan
    INSERT INTO dw.DimAnggota (IdAnggota, NamaAnggota, Email)
    SELECT c.CustomerId, c.FullName, c.EmailAddress
    FROM stg.RawCustomers c
    LEFT JOIN dw.DimAnggota d ON c.CustomerId = d.IdAnggota
    WHERE d.AnggotaKey IS NULL;

    INSERT INTO dw.FactSimpanan (AnggotaKey, JumlahSimpanan, TanggalTransaksi)
    SELECT d.AnggotaKey, p.JumlahPinjaman, GETDATE()
    FROM dbo.MasterPinjaman p
    INNER JOIN dw.DimAnggota d ON p.IdAnggota = d.IdAnggota
    WHERE p.StatusPinjaman = 'ACTIVE';
END
GO

-- ── Insert Sample Data ────────────────────────────────────
INSERT INTO stg.RawCustomers (CustomerId, FullName, EmailAddress) VALUES
(101, 'Budi Santoso', 'budi@example.com'),
(102, 'Siti Aminah', 'siti@example.com');

INSERT INTO dbo.MasterPinjaman (IdPinjaman, IdAnggota, OsPokok, JumlahPinjaman) VALUES
(5001, 101, 2500000.00, 5000000.00),
(5002, 102, 1000000.00, 3000000.00);

EXEC dbo.sp_ProcessLoanLineage;
GO
