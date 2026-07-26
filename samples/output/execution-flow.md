# SSIS Project Lineage Documentation
Generated on: 2026-07-26 19:33:42

## Packages Included
- **CreateSalesForecastInput** (GUID: `CreateSalesForecastInput`)
  - Path: `/home/hirazone/Documents/SSIS-Project-Documentation/samples/samples/Tutorial-Sample-1/CreateSalesForecastInput.dtsx`

## Tasks
| Package | Task | Type | Narasi Bisnis |
| --- | --- | --- | --- |
| CreateSalesForecastInput | Create Sales Forecast Input Spreadsheet | `DTS.Pipeline.1` | Aliran Data (Data Flow): Mengambil data dari `Kota` (`select
  g.GeographyKey
, g.SalesTerritoryKey  
, g.City
, g.StateProvinceName [State]
, g.PostalCode
from dbo.DimGeography g
join dbo.DimSalesTerritory t 
  on t.SalesTerritoryKey = g.SalesTerritoryKey
where t.SalesTerritoryCountry = 'United States' 
order by g.StateProvinceName, g.City`), diproses melalui transformasi Third-Party: Derived Column, dan memuat hasilnya ke `Excel Destination`. |

## Data Flow Components
| Package | Task | Component | Type | Connection | SQL / Table | Narasi Bisnis |
| --- | --- | --- | --- | --- | --- | --- |
| CreateSalesForecastInput | Create Sales Forecast Input Spreadsheet | OLE DB Source | `Third-Party: OLE DB Source` | `{F7E88C74-D039-4BB7-BBAF-672D8AF85932}` | `select
  g.GeographyKey
, g.SalesTerritoryKey  
, g.City
, g.StateProvinceName [State]
, g.PostalCode
from dbo.DimGeography g
join dbo.DimSalesTerritory t 
  on t.SalesTerritoryKey = g.SalesTerritoryKey
where t.SalesTerritoryCountry = 'United States' 
order by g.StateProvinceName, g.City` | Mengambil data awal (Source) dari `Kota` (`select
  g.GeographyKey
, g.SalesTerritoryKey  
, g.City
, g.StateProvinceName [State]
, g.PostalCode
from dbo.DimGeography g
join dbo.DimSalesTerritory t 
  on t.SalesTerritoryKey = g.SalesTerritoryKey
where t.SalesTerritoryCountry = 'United States' 
order by g.StateProvinceName, g.City`) untuk diproses dalam alur data. |
| CreateSalesForecastInput | Create Sales Forecast Input Spreadsheet | Derived Column | `Third-Party: Derived Column` | `` | `` | Membuat atau memperbarui kolom baru (Derived Column) (`Prakiraan Penjualan`) menggunakan ekspresi logika bisnis. |
| CreateSalesForecastInput | Create Sales Forecast Input Spreadsheet | Excel Destination | `Third-Party: Excel Destination` | `{02EFA3C0-9DBE-4279-9E03-B752C6523DAE}` | `Excel Destination` | Memuat hasil pemrosesan data (Destination) ke `Excel Destination`. |

## Data Flow Paths
| From Component | To Component | Path |
| --- | --- | --- |
| `OLE DB Source` | `Derived Column` | `127` |
| `Derived Column` | `Excel Destination` | `142` |

## Column-level Lineage Mappings
| Package | Task | Source Component | Source Column | Destination Component | Destination Column | Operation |
| --- | --- | --- | --- | --- | --- | --- |
| CreateSalesForecastInput | Create Sales Forecast Input Spreadsheet | OLE DB Source | `GeographyKey` | Excel Destination | `GeographyKey` | XML_FALLBACK |
| CreateSalesForecastInput | Create Sales Forecast Input Spreadsheet | OLE DB Source | `SalesTerritoryKey` | Excel Destination | `SalesTerritoryKey` | XML_FALLBACK |
| CreateSalesForecastInput | Create Sales Forecast Input Spreadsheet | OLE DB Source | `City` | Excel Destination | `City` | XML_FALLBACK |
| CreateSalesForecastInput | Create Sales Forecast Input Spreadsheet | OLE DB Source | `State` | Excel Destination | `State` | XML_FALLBACK |
| CreateSalesForecastInput | Create Sales Forecast Input Spreadsheet | OLE DB Source | `PostalCode` | Excel Destination | `PostalCode` | XML_FALLBACK |
| CreateSalesForecastInput | Create Sales Forecast Input Spreadsheet | Derived Column | `Forecast` | Excel Destination | `Forecast` | XML_FALLBACK |
