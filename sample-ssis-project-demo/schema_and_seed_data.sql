-- =============================================================================
-- FILE   : schema_and_seed_data.sql
-- PURPOSE: DDL + seed data untuk simulasi paket SSIS Pkg_SalesOrder_ETL.dtsx
--          Jalankan skrip ini di SQL Server terlebih dahulu sebelum membuka
--          paket SSIS agar semua tabel tersedia.
--
-- DATABASE SOURCE  : SalesSourceDB   (OLTP – sumber data)
-- DATABASE TARGET  : SalesWarehouseDB (staging / DW – tujuan data)
-- =============================================================================

-- ─────────────────────────────────────────────────────────────────────────────
-- 0. Pastikan kedua database ada
-- ─────────────────────────────────────────────────────────────────────────────
IF DB_ID('SalesSourceDB') IS NULL
    CREATE DATABASE SalesSourceDB;
GO

IF DB_ID('SalesWarehouseDB') IS NULL
    CREATE DATABASE SalesWarehouseDB;
GO

-- =============================================================================
-- BAGIAN 1 — DATABASE SOURCE: SalesSourceDB
-- =============================================================================
USE SalesSourceDB;
GO

-- ─────────────────────────────────────────────────────────────────────────────
-- 1.1  Tabel dbo.Products  (master produk)
-- ─────────────────────────────────────────────────────────────────────────────
IF OBJECT_ID('dbo.Products', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Products (
        ProductId      INT           NOT NULL PRIMARY KEY,
        ProductCode    NVARCHAR(20)  NOT NULL UNIQUE,
        ProductName    NVARCHAR(100) NOT NULL,
        CategoryCode   NVARCHAR(20)  NOT NULL,   -- FK ke dbo.ProductCategories
        UnitPrice      DECIMAL(18,2) NOT NULL,
        IsActive       BIT           NOT NULL DEFAULT 1
    );
END;
GO

-- ─────────────────────────────────────────────────────────────────────────────
-- 1.2  Tabel dbo.ProductCategories  (lookup)
-- ─────────────────────────────────────────────────────────────────────────────
IF OBJECT_ID('dbo.ProductCategories', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProductCategories (
        CategoryCode   NVARCHAR(20)  NOT NULL PRIMARY KEY,
        CategoryName   NVARCHAR(100) NOT NULL,
        Department     NVARCHAR(50)  NOT NULL
    );
END;
GO

-- ─────────────────────────────────────────────────────────────────────────────
-- 1.3  Tabel dbo.Customers  (master pelanggan)
-- ─────────────────────────────────────────────────────────────────────────────
IF OBJECT_ID('dbo.Customers', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Customers (
        CustomerId     INT           NOT NULL PRIMARY KEY,
        CustomerCode   NVARCHAR(20)  NOT NULL UNIQUE,
        FullName       NVARCHAR(100) NOT NULL,
        RegionCode     NVARCHAR(10)  NOT NULL,   -- FK ke dbo.SalesRegions
        Email          NVARCHAR(150) NULL,
        Phone          NVARCHAR(30)  NULL,
        CreditLimit    DECIMAL(18,2) NOT NULL DEFAULT 5000000.00,
        JoinDate       DATE          NOT NULL
    );
END;
GO

-- ─────────────────────────────────────────────────────────────────────────────
-- 1.4  Tabel dbo.SalesRegions  (lookup)
-- ─────────────────────────────────────────────────────────────────────────────
IF OBJECT_ID('dbo.SalesRegions', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.SalesRegions (
        RegionCode     NVARCHAR(10)  NOT NULL PRIMARY KEY,
        RegionName     NVARCHAR(100) NOT NULL,
        ZoneCode       NVARCHAR(5)   NOT NULL    -- WIB / WITA / WIT
    );
END;
GO

-- ─────────────────────────────────────────────────────────────────────────────
-- 1.5  Tabel dbo.SalesOrders  (transaksi utama – tabel sumber utama)
-- ─────────────────────────────────────────────────────────────────────────────
IF OBJECT_ID('dbo.SalesOrders', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.SalesOrders (
        OrderId        INT           NOT NULL PRIMARY KEY,
        OrderDate      DATE          NOT NULL,
        CustomerId     INT           NOT NULL,
        ProductId      INT           NOT NULL,
        Quantity       INT           NOT NULL DEFAULT 1,
        UnitPrice      DECIMAL(18,2) NOT NULL,
        DiscountPct    DECIMAL(5,2)  NOT NULL DEFAULT 0.00,
        StatusCode     NVARCHAR(20)  NOT NULL,   -- PENDING / CONFIRMED / SHIPPED / COMPLETED / CANCELLED
        CreatedAt      DATETIME      NOT NULL DEFAULT GETDATE(),
        UpdatedAt      DATETIME      NOT NULL DEFAULT GETDATE()
    );
END;
GO

-- ─────────────────────────────────────────────────────────────────────────────
-- 1.6  Tabel dbo.OrderStatuses  (lookup status order)
-- ─────────────────────────────────────────────────────────────────────────────
IF OBJECT_ID('dbo.OrderStatuses', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.OrderStatuses (
        StatusCode     NVARCHAR(20)  NOT NULL PRIMARY KEY,
        StatusLabel    NVARCHAR(50)  NOT NULL,
        IsFinal        BIT           NOT NULL DEFAULT 0
    );
END;
GO

-- =============================================================================
-- BAGIAN 2 — SEED DATA untuk SalesSourceDB
-- =============================================================================

-- 2.1 ProductCategories
IF NOT EXISTS (SELECT 1 FROM dbo.ProductCategories)
BEGIN
    INSERT INTO dbo.ProductCategories (CategoryCode, CategoryName, Department) VALUES
        ('CAT_ELEC',  'Electronics',      'Technology'),
        ('CAT_FURN',  'Furniture',        'Office Supply'),
        ('CAT_FOOD',  'Food & Beverage',  'Consumer Goods'),
        ('CAT_APRL',  'Apparel',          'Fashion'),
        ('CAT_TOOL',  'Tools & Hardware', 'Industrial');
END;
GO

-- 2.2 Products
IF NOT EXISTS (SELECT 1 FROM dbo.Products)
BEGIN
    INSERT INTO dbo.Products (ProductId, ProductCode, ProductName, CategoryCode, UnitPrice) VALUES
        (1,  'PRD-1001', 'Laptop Ultrabook 14"',        'CAT_ELEC', 12500000.00),
        (2,  'PRD-1002', 'Mechanical Keyboard TKL',     'CAT_ELEC',   850000.00),
        (3,  'PRD-1003', 'Office Chair Ergonomic',      'CAT_FURN',  3200000.00),
        (4,  'PRD-1004', 'Standing Desk Adjustable',    'CAT_FURN',  4800000.00),
        (5,  'PRD-1005', 'Premium Coffee Beans 500g',   'CAT_FOOD',   185000.00),
        (6,  'PRD-1006', 'Polo Shirt Corporate',        'CAT_APRL',   120000.00),
        (7,  'PRD-1007', 'Cordless Drill 18V',          'CAT_TOOL',   950000.00),
        (8,  'PRD-1008', 'Wireless Headset Noise-Cancel','CAT_ELEC', 1750000.00),
        (9,  'PRD-1009', 'Bookshelf 5-Tier',            'CAT_FURN',  1100000.00),
        (10, 'PRD-1010', 'Instant Noodle Box (40pcs)',  'CAT_FOOD',   120000.00);
END;
GO

-- 2.3 SalesRegions
IF NOT EXISTS (SELECT 1 FROM dbo.SalesRegions)
BEGIN
    INSERT INTO dbo.SalesRegions (RegionCode, RegionName, ZoneCode) VALUES
        ('REG_JKT',  'DKI Jakarta',       'WIB'),
        ('REG_BDG',  'Jawa Barat',        'WIB'),
        ('REG_SBY',  'Jawa Timur',        'WIB'),
        ('REG_MKS',  'Sulawesi Selatan',  'WITA'),
        ('REG_DPS',  'Bali',              'WITA'),
        ('REG_MDN',  'Sumatera Utara',    'WIB'),
        ('REG_JYP',  'D.I. Yogyakarta',   'WIB');
END;
GO

-- 2.4 Customers
IF NOT EXISTS (SELECT 1 FROM dbo.Customers)
BEGIN
    INSERT INTO dbo.Customers (CustomerId, CustomerCode, FullName, RegionCode, Email, Phone, CreditLimit, JoinDate) VALUES
        (1,  'CUST-001', 'PT Maju Bersama',          'REG_JKT', 'procurement@majubersama.co.id',  '021-55512345', 100000000.00, '2022-01-15'),
        (2,  'CUST-002', 'CV Teknologi Nusantara',   'REG_BDG', 'order@teknologi-nusantara.com',  '022-77799001',  50000000.00, '2022-03-20'),
        (3,  'CUST-003', 'UD Cahaya Timur',          'REG_SBY', 'cahayatimur@gmail.com',          '031-88812321',  25000000.00, '2022-06-10'),
        (4,  'CUST-004', 'PT Bintang Makassar',      'REG_MKS', 'info@bintangmks.co.id',         '0411-334455',   75000000.00, '2023-01-05'),
        (5,  'CUST-005', 'Toko Bali Indah',          'REG_DPS', 'baliindah@yahoo.com',            '0361-556677',   15000000.00, '2023-04-18'),
        (6,  'CUST-006', 'PT Sumatra Retail Group',  'REG_MDN', 'retail@srgmedan.com',            '061-445566',    60000000.00, '2023-07-22'),
        (7,  'CUST-007', 'UD Jogja Craft',           'REG_JYP', 'jogjacraft@craft.id',            '0274-889900',   10000000.00, '2024-02-14');
END;
GO

-- 2.5 OrderStatuses
IF NOT EXISTS (SELECT 1 FROM dbo.OrderStatuses)
BEGIN
    INSERT INTO dbo.OrderStatuses (StatusCode, StatusLabel, IsFinal) VALUES
        ('PENDING',    'Menunggu Konfirmasi',  0),
        ('CONFIRMED',  'Dikonfirmasi',         0),
        ('SHIPPED',    'Dikirim',              0),
        ('COMPLETED',  'Selesai',              1),
        ('CANCELLED',  'Dibatalkan',           1);
END;
GO

-- 2.6 SalesOrders  (20 baris transaksi simulasi)
IF NOT EXISTS (SELECT 1 FROM dbo.SalesOrders)
BEGIN
    INSERT INTO dbo.SalesOrders (OrderId, OrderDate, CustomerId, ProductId, Quantity, UnitPrice, DiscountPct, StatusCode) VALUES
        (1001, '2026-01-05', 1, 1,  2, 12500000.00,  5.00, 'COMPLETED'),
        (1002, '2026-01-07', 2, 2,  5,   850000.00,  0.00, 'COMPLETED'),
        (1003, '2026-01-10', 3, 5, 10,   185000.00,  0.00, 'COMPLETED'),
        (1004, '2026-01-12', 4, 8,  3,  1750000.00, 10.00, 'SHIPPED'),
        (1005, '2026-01-15', 5, 6, 20,   120000.00,  0.00, 'COMPLETED'),
        (1006, '2026-01-20', 1, 3,  4,  3200000.00,  7.50, 'COMPLETED'),
        (1007, '2026-01-22', 6, 7,  6,   950000.00,  0.00, 'CONFIRMED'),
        (1008, '2026-01-25', 2, 4,  2,  4800000.00, 15.00, 'COMPLETED'),
        (1009, '2026-02-01', 7, 10,50,   120000.00,  0.00, 'COMPLETED'),
        (1010, '2026-02-03', 3, 1,  1, 12500000.00,  0.00, 'CANCELLED'),
        (1011, '2026-02-10', 4, 9,  3,  1100000.00,  5.00, 'COMPLETED'),
        (1012, '2026-02-14', 5, 2,  4,   850000.00,  0.00, 'SHIPPED'),
        (1013, '2026-02-18', 1, 8,  5,  1750000.00, 10.00, 'COMPLETED'),
        (1014, '2026-02-22', 6, 1,  1, 12500000.00, 20.00, 'CONFIRMED'),
        (1015, '2026-03-01', 7, 3,  2,  3200000.00,  0.00, 'COMPLETED'),
        (1016, '2026-03-05', 2, 7, 10,   950000.00,  5.00, 'COMPLETED'),
        (1017, '2026-03-10', 3, 5, 30,   185000.00,  0.00, 'PENDING'),
        (1018, '2026-03-12', 4, 4,  1,  4800000.00,  8.00, 'SHIPPED'),
        (1019, '2026-03-15', 5, 6, 15,   120000.00,  0.00, 'COMPLETED'),
        (1020, '2026-03-20', 1, 7,  8,   950000.00, 12.00, 'COMPLETED');
END;
GO

-- =============================================================================
-- BAGIAN 3 — DATABASE TARGET: SalesWarehouseDB
-- =============================================================================
USE SalesWarehouseDB;
GO

-- ─────────────────────────────────────────────────────────────────────────────
-- 3.1  Tabel staging stg.RawSalesOrders
--      Didrop-dan-recreate setiap run (idempotent via SSIS SQL Task)
-- ─────────────────────────────────────────────────────────────────────────────
IF SCHEMA_ID('stg') IS NULL
    EXEC('CREATE SCHEMA stg');
GO

IF OBJECT_ID('stg.RawSalesOrders', 'U') IS NOT NULL
    DROP TABLE stg.RawSalesOrders;
GO

CREATE TABLE stg.RawSalesOrders (
    OrderId         INT            NULL,
    OrderDate       DATE           NULL,
    CustomerCode    NVARCHAR(20)   NULL,
    CustomerName    NVARCHAR(100)  NULL,
    RegionName      NVARCHAR(100)  NULL,    -- hasil Lookup dari SalesRegions
    ZoneCode        NVARCHAR(5)    NULL,    -- hasil Lookup dari SalesRegions
    ProductCode     NVARCHAR(20)   NULL,
    ProductName     NVARCHAR(100)  NULL,
    CategoryName    NVARCHAR(100)  NULL,    -- hasil Lookup dari ProductCategories
    Department      NVARCHAR(50)   NULL,    -- hasil Lookup dari ProductCategories
    Quantity        INT            NULL,
    UnitPrice       DECIMAL(18,2)  NULL,
    DiscountPct     DECIMAL(5,2)   NULL,
    GrossAmount     DECIMAL(18,2)  NULL,    -- Quantity * UnitPrice (Derived Column)
    NetAmount       DECIMAL(18,2)  NULL,    -- GrossAmount * (1 - DiscountPct/100) (Derived Column)
    StatusCode      NVARCHAR(20)   NULL,
    StatusLabel     NVARCHAR(50)   NULL,    -- hasil Lookup dari OrderStatuses
    IsFinalStatus   BIT            NULL,    -- hasil Lookup dari OrderStatuses
    EtlLoadedAt     DATETIME       NOT NULL DEFAULT GETDATE(),
    EtlBatchDate    DATE           NULL     -- dari variabel paket SSIS
);
GO

-- ─────────────────────────────────────────────────────────────────────────────
-- 3.2  Tabel dw.FactSalesOrders  (final DW table, diisi dari staging)
-- ─────────────────────────────────────────────────────────────────────────────
IF SCHEMA_ID('dw') IS NULL
    EXEC('CREATE SCHEMA dw');
GO

IF OBJECT_ID('dw.FactSalesOrders', 'U') IS NULL
BEGIN
    CREATE TABLE dw.FactSalesOrders (
        FactId          INT           NOT NULL IDENTITY(1,1) PRIMARY KEY,
        OrderId         INT           NOT NULL,
        OrderDate       DATE          NOT NULL,
        CustomerCode    NVARCHAR(20)  NOT NULL,
        CustomerName    NVARCHAR(100) NOT NULL,
        RegionName      NVARCHAR(100) NOT NULL,
        ZoneCode        NVARCHAR(5)   NOT NULL,
        ProductCode     NVARCHAR(20)  NOT NULL,
        ProductName     NVARCHAR(100) NOT NULL,
        CategoryName    NVARCHAR(100) NOT NULL,
        Department      NVARCHAR(50)  NOT NULL,
        Quantity        INT           NOT NULL,
        UnitPrice       DECIMAL(18,2) NOT NULL,
        DiscountPct     DECIMAL(5,2)  NOT NULL,
        GrossAmount     DECIMAL(18,2) NOT NULL,
        NetAmount       DECIMAL(18,2) NOT NULL,
        StatusCode      NVARCHAR(20)  NOT NULL,
        StatusLabel     NVARCHAR(50)  NOT NULL,
        IsFinalStatus   BIT           NOT NULL DEFAULT 0,
        EtlBatchDate    DATE          NOT NULL,
        LoadedAt        DATETIME      NOT NULL DEFAULT GETDATE()
    );
END;
GO

PRINT '============================================================';
PRINT 'Schema dan seed data berhasil dibuat.';
PRINT '  SalesSourceDB : dbo.Products, ProductCategories, Customers,';
PRINT '                  SalesRegions, OrderStatuses, SalesOrders (20 baris)';
PRINT '  SalesWarehouseDB: stg.RawSalesOrders, dw.FactSalesOrders';
PRINT '============================================================';
GO
