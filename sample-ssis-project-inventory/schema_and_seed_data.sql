-- =============================================================================
-- Inventory ETL Sample Project — Schema & Seed Data
-- Source DB  : InventorySourceDB  (OLTP — movement records + master data)
-- Target DB  : InventoryWarehouseDB (Staging/DW — raw landing table)
-- =============================================================================

-- =============================================================================
-- SECTION 1: SOURCE DATABASE
-- =============================================================================
USE master;
GO

IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = 'InventorySourceDB')
    CREATE DATABASE InventorySourceDB;
GO
USE InventorySourceDB;
GO

-- ── Master: Products ──────────────────────────────────────────────────────────
IF OBJECT_ID('dbo.Products', 'U') IS NOT NULL DROP TABLE dbo.Products;
CREATE TABLE dbo.Products (
    ProductId     INT           PRIMARY KEY,
    ProductCode   NVARCHAR(20)  NOT NULL,
    ProductName   NVARCHAR(100) NOT NULL,
    CategoryId    INT           NOT NULL,
    ReorderLevel  INT           NOT NULL DEFAULT 50,
    UnitOfMeasure NVARCHAR(20)  NOT NULL DEFAULT 'PCS'
);

INSERT INTO dbo.Products VALUES
(1, 'PRD-001', 'Industrial Bearing 6205',    1, 100, 'PCS'),
(2, 'PRD-002', 'Hydraulic Pump HYD-200',     1, 30,  'UNIT'),
(3, 'PRD-003', 'Safety Gloves L-Type',       2, 200, 'PAIR'),
(4, 'PRD-004', 'Welding Rod E6013 5kg',      3, 50,  'BOX'),
(5, 'PRD-005', 'Drill Bit Set 13pc',         1, 80,  'SET'),
(6, 'PRD-006', 'Safety Helmet Class A',      2, 150, 'PCS'),
(7, 'PRD-007', 'Lubricant Oil 5L',           4, 60,  'CAN'),
(8, 'PRD-008', 'Cable Tie 300mm Bag',        3, 120, 'BAG'),
(9, 'PRD-009', 'Pressure Gauge 0-10 Bar',    1, 40,  'PCS'),
(10,'PRD-010', 'Electrical Tape 19mm',       3, 300, 'ROLL');

-- ── Master: ProductCategories ─────────────────────────────────────────────────
IF OBJECT_ID('dbo.ProductCategories', 'U') IS NOT NULL DROP TABLE dbo.ProductCategories;
CREATE TABLE dbo.ProductCategories (
    CategoryId   INT          PRIMARY KEY,
    CategoryCode NVARCHAR(20) NOT NULL,
    CategoryName NVARCHAR(100) NOT NULL
);
INSERT INTO dbo.ProductCategories VALUES
(1, 'MECH',  'Mechanical Parts'),
(2, 'SAFETY','Safety Equipment'),
(3, 'CONSUM','Consumables'),
(4, 'LUBRIC','Lubricants & Chemicals');

-- ── Master: Warehouses ────────────────────────────────────────────────────────
IF OBJECT_ID('dbo.Warehouses', 'U') IS NOT NULL DROP TABLE dbo.Warehouses;
CREATE TABLE dbo.Warehouses (
    WarehouseId   INT          PRIMARY KEY,
    WarehouseCode NVARCHAR(20) NOT NULL,
    WarehouseName NVARCHAR(100) NOT NULL,
    LocationCity  NVARCHAR(100) NOT NULL
);
INSERT INTO dbo.Warehouses VALUES
(1, 'WH-JKT', 'Main Warehouse Jakarta',   'Jakarta'),
(2, 'WH-SBY', 'Surabaya Distribution Hub','Surabaya'),
(3, 'WH-MDN', 'Medan Regional Store',     'Medan');

-- ── Master: Suppliers ─────────────────────────────────────────────────────────
IF OBJECT_ID('dbo.Suppliers', 'U') IS NOT NULL DROP TABLE dbo.Suppliers;
CREATE TABLE dbo.Suppliers (
    SupplierId    INT          PRIMARY KEY,
    SupplierCode  NVARCHAR(20) NOT NULL,
    SupplierName  NVARCHAR(200) NOT NULL,
    LeadTimeDays  INT          NOT NULL DEFAULT 7
);
INSERT INTO dbo.Suppliers VALUES
(1, 'SUP-001', 'PT Maju Bersama Teknik',     5),
(2, 'SUP-002', 'CV Indo Spare Parts',         3),
(3, 'SUP-003', 'PT Safety First Indonesia',   7),
(4, 'SUP-004', 'Global Industrial Supply',    14);

-- ── Fact: StockMovements ──────────────────────────────────────────────────────
IF OBJECT_ID('dbo.StockMovements', 'U') IS NOT NULL DROP TABLE dbo.StockMovements;
CREATE TABLE dbo.StockMovements (
    MovementId   INT            PRIMARY KEY,
    MovementDate DATE           NOT NULL,
    ProductId    INT            NOT NULL,
    WarehouseId  INT            NOT NULL,
    SupplierId   INT            NOT NULL,
    MovementType NVARCHAR(20)   NOT NULL,  -- IN / OUT / ADJUST
    Quantity     INT            NOT NULL,
    UnitCost     DECIMAL(18,2)  NOT NULL,
    ReferenceNo  NVARCHAR(50)   NOT NULL
);
INSERT INTO dbo.StockMovements VALUES
(1,  '2026-01-05', 1, 1, 1, 'IN',     200, 25000.00,  'PO-2026-001'),
(2,  '2026-01-06', 2, 1, 2, 'IN',      50, 850000.00, 'PO-2026-002'),
(3,  '2026-01-08', 3, 2, 3, 'IN',     500, 35000.00,  'PO-2026-003'),
(4,  '2026-01-10', 1, 1, 1, 'OUT',     80, 25000.00,  'DO-2026-001'),
(5,  '2026-01-12', 4, 1, 1, 'IN',     100, 95000.00,  'PO-2026-004'),
(6,  '2026-01-15', 5, 2, 2, 'IN',      60, 185000.00, 'PO-2026-005'),
(7,  '2026-01-18', 2, 1, 2, 'OUT',     10, 850000.00, 'DO-2026-002'),
(8,  '2026-01-20', 6, 3, 3, 'IN',     300, 75000.00,  'PO-2026-006'),
(9,  '2026-01-22', 7, 1, 4, 'IN',      50, 145000.00, 'PO-2026-007'),
(10, '2026-01-25', 3, 2, 3, 'OUT',    200, 35000.00,  'DO-2026-003'),
(11, '2026-01-28', 8, 1, 2, 'IN',     400, 18500.00,  'PO-2026-008'),
(12, '2026-02-01', 9, 1, 1, 'IN',      75, 220000.00, 'PO-2026-009'),
(13, '2026-02-03', 10,2, 2, 'IN',    1000, 8500.00,   'PO-2026-010'),
(14, '2026-02-05', 1, 1, 1, 'ADJUST', -20, 25000.00,  'ADJ-2026-001'),
(15, '2026-02-08', 4, 3, 1, 'IN',     150, 95000.00,  'PO-2026-011'),
(16, '2026-02-10', 6, 2, 3, 'OUT',     50, 75000.00,  'DO-2026-004'),
(17, '2026-02-12', 5, 1, 2, 'OUT',     20, 185000.00, 'DO-2026-005'),
(18, '2026-02-15', 7, 1, 4, 'OUT',     15, 145000.00, 'DO-2026-006'),
(19, '2026-02-18', 2, 2, 2, 'IN',      25, 850000.00, 'PO-2026-012'),
(20, '2026-02-20', 9, 1, 1, 'OUT',     30, 220000.00, 'DO-2026-007');

-- =============================================================================
-- SECTION 2: TARGET DATABASE (Warehouse)
-- =============================================================================
USE master;
GO

IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = 'InventoryWarehouseDB')
    CREATE DATABASE InventoryWarehouseDB;
GO
USE InventoryWarehouseDB;
GO

-- ── Staging landing table ─────────────────────────────────────────────────────
IF OBJECT_ID('dbo.RawStockMovements', 'U') IS NOT NULL DROP TABLE dbo.RawStockMovements;
CREATE TABLE dbo.RawStockMovements (
    MovementId   INT            NOT NULL,
    MovementDate DATE           NOT NULL,
    ProductCode  NVARCHAR(20)   NOT NULL,
    ProductName  NVARCHAR(100)  NOT NULL,
    CategoryName NVARCHAR(100),
    CategoryCode NVARCHAR(20),
    WarehouseCode NVARCHAR(20)  NOT NULL,
    WarehouseName NVARCHAR(100),
    LocationCity  NVARCHAR(100),
    SupplierCode  NVARCHAR(20)  NOT NULL,
    SupplierName  NVARCHAR(200),
    MovementType  NVARCHAR(20)  NOT NULL,
    Quantity      INT            NOT NULL,
    UnitCost      DECIMAL(18,2)  NOT NULL,
    TotalCost     DECIMAL(18,2),
    IsLowStock    BIT,
    EtlBatchDate  DATE,
    ReferenceNo   NVARCHAR(50)
);
GO
PRINT 'InventoryWarehouseDB.dbo.RawStockMovements created.';
GO
