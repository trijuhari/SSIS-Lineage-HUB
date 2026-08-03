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
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'external') EXEC('CREATE SCHEMA [external]');
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'stg') EXEC('CREATE SCHEMA stg');
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'dw')  EXEC('CREATE SCHEMA dw');
GO

-- ── Tables ────────────────────────────────────────────────
IF OBJECT_ID('external.CrmCustomers', 'U') IS NOT NULL DROP TABLE [external].CrmCustomers;
CREATE TABLE [external].CrmCustomers (
    CustomerId INT PRIMARY KEY,
    FullName VARCHAR(100),
    EmailAddress VARCHAR(150),
    PhoneNumber VARCHAR(20)
);

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

-- ── Insert Sample Data (Large Dataset Generation) ─────────
SET NOCOUNT ON;
DECLARE @i INT = 1;
DECLARE @max INT = 2500; -- Men-generate 2500 data pelanggan & pinjaman

PRINT 'Generating 2500 sample records... This might take a few seconds.'

WHILE @i <= @max
BEGIN
    INSERT INTO [external].CrmCustomers (CustomerId, FullName, EmailAddress, PhoneNumber)
    VALUES (
        @i,
        'Pelanggan ' + CAST(@i AS VARCHAR),
        'pelanggan' + CAST(@i AS VARCHAR) + '@demo-enterprise.com',
        '0812' + RIGHT('0000000' + CAST(CAST(RAND()*10000000 AS INT) AS VARCHAR), 7)
    );

    INSERT INTO stg.RawCustomers (CustomerId, FullName, EmailAddress)
    VALUES (
        @i,
        'Pelanggan ' + CAST(@i AS VARCHAR),
        'pelanggan' + CAST(@i AS VARCHAR) + '@demo-enterprise.com'
    );

    INSERT INTO dbo.MasterPinjaman (IdPinjaman, IdAnggota, OsPokok, JumlahPinjaman, StatusPinjaman)
    VALUES (
        5000 + @i,
        @i,
        ROUND(RAND() * 5000000, 2),
        ROUND((RAND() * 5000000) + 5000000, 2),
        CASE WHEN RAND() > 0.15 THEN 'ACTIVE' ELSE 'PAID' END
    );

    SET @i = @i + 1;
END
SET NOCOUNT OFF;

EXEC dbo.sp_ProcessLoanLineage;
GO
