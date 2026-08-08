-- ============================================================================
-- SQL Server Initialization Script for sample-ssis-project-7
-- Domain: Enterprise Inventory & Logistics Analytics System
-- ============================================================================

-- 1. Create Databases
IF NOT EXISTS (SELECT * FROM sys.databases WHERE name = 'SupplyChainDB')
    CREATE DATABASE SupplyChainDB;
GO

IF NOT EXISTS (SELECT * FROM sys.databases WHERE name = 'WarehouseWmsDB')
    CREATE DATABASE WarehouseWmsDB;
GO

IF NOT EXISTS (SELECT * FROM sys.databases WHERE name = 'EnterpriseDataWarehouse')
    CREATE DATABASE EnterpriseDataWarehouse;
GO

-- 2. Populate WarehouseWmsDB
USE WarehouseWmsDB;
GO

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[InventoryItem]') AND type in (N'U'))
BEGIN
    CREATE TABLE dbo.InventoryItem (
        ItemID INT IDENTITY(1,1) PRIMARY KEY,
        ItemSKU VARCHAR(50) NOT NULL,
        WarehouseCode VARCHAR(20) NOT NULL,
        StockQuantity INT NOT NULL,
        ReorderLevel INT NOT NULL,
        LastRestockDate DATETIME NOT NULL
    );

    INSERT INTO dbo.InventoryItem (ItemSKU, WarehouseCode, StockQuantity, ReorderLevel, LastRestockDate) VALUES
    ('SKU-LOG-1001', 'JKT-WH-01', 1200, 200, '2026-08-01 08:30:00'),
    ('SKU-LOG-1002', 'SUB-WH-02', 850, 150, '2026-08-02 09:15:00'),
    ('SKU-LOG-1003', 'BDG-WH-03', 430, 100, '2026-08-03 10:45:00'),
    ('SKU-LOG-1004', 'JKT-WH-01', 2100, 500, '2026-08-04 14:20:00');
END;
GO

-- 3. Populate SupplyChainDB
USE SupplyChainDB;
GO

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[ShipmentOrder]') AND type in (N'U'))
BEGIN
    CREATE TABLE dbo.ShipmentOrder (
        ShipmentID INT IDENTITY(1,1) PRIMARY KEY,
        TrackingNumber VARCHAR(50) NOT NULL,
        CarrierCode VARCHAR(20) NOT NULL,
        OriginCity VARCHAR(50) NOT NULL,
        DestinationCity VARCHAR(50) NOT NULL,
        ShippingCost DECIMAL(18,2) NOT NULL,
        DeliveryStatus VARCHAR(30) NOT NULL,
        OrderDate DATETIME NOT NULL
    );

    INSERT INTO dbo.ShipmentOrder (TrackingNumber, CarrierCode, OriginCity, DestinationCity, ShippingCost, DeliveryStatus, OrderDate) VALUES
    ('TRK-IND-8801', 'JNE-EXP', 'JKT-WH-01', 'Surabaya', 150000.00, 'DELIVERED', '2026-08-01 09:00:00'),
    ('TRK-IND-8802', 'JNT-EXPRESS', 'SUB-WH-02', 'Medan', 275000.00, 'IN_TRANSIT', '2026-08-02 11:30:00'),
    ('TRK-IND-8803', 'POS-INDONESIA', 'BDG-WH-03', 'Semarang', 95000.00, 'DELIVERED', '2026-08-03 15:10:00'),
    ('TRK-IND-8804', 'JNE-EXP', 'JKT-WH-01', 'Makassar', 320000.00, 'PROCESSING', '2026-08-04 16:45:00');
END;
GO

-- 4. Populate EnterpriseDataWarehouse
USE EnterpriseDataWarehouse;
GO

IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'dim')
    EXEC('CREATE SCHEMA dim');
GO

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dim].[Warehouse]') AND type in (N'U'))
BEGIN
    CREATE TABLE dim.Warehouse (
        WarehouseKey INT IDENTITY(1,1) PRIMARY KEY,
        WarehouseCode VARCHAR(20) NOT NULL,
        WarehouseName VARCHAR(100) NOT NULL,
        City VARCHAR(50) NOT NULL
    );

    INSERT INTO dim.Warehouse (WarehouseCode, WarehouseName, City) VALUES
    ('JKT-WH-01', 'Jakarta Central Warehouse', 'Jakarta'),
    ('SUB-WH-02', 'Surabaya Hub Warehouse', 'Surabaya'),
    ('BDG-WH-03', 'Bandung Logistics Center', 'Bandung');
END;
GO

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dim].[Carrier]') AND type in (N'U'))
BEGIN
    CREATE TABLE dim.Carrier (
        CarrierKey INT IDENTITY(1,1) PRIMARY KEY,
        CarrierCode VARCHAR(20) NOT NULL,
        CarrierName VARCHAR(100) NOT NULL
    );

    INSERT INTO dim.Carrier (CarrierCode, CarrierName) VALUES
    ('JNE-EXP', 'JNE Express Logistics'),
    ('JNT-EXPRESS', 'J&T Express Logistics'),
    ('POS-INDONESIA', 'Pos Indonesia Logistics');
END;
GO

IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dim].[Date]') AND type in (N'U'))
BEGIN
    CREATE TABLE dim.Date (
        DateKey INT PRIMARY KEY,
        FullDate DATETIME NOT NULL,
        Year INT NOT NULL,
        Month INT NOT NULL,
        Day INT NOT NULL
    );

    INSERT INTO dim.Date (DateKey, FullDate, Year, Month, Day) VALUES
    (20260801, '2026-08-01 00:00:00', 2026, 8, 1),
    (20260802, '2026-08-02 00:00:00', 2026, 8, 2),
    (20260803, '2026-08-03 00:00:00', 2026, 8, 3),
    (20260804, '2026-08-04 00:00:00', 2026, 8, 4);
END;
GO
