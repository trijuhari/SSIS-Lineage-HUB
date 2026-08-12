-- dbt Model: stg_single_customer_etl.sql
-- Converted from SSIS Package: Pkg_Single_Customer_ETL

WITH source_data AS (
    -- Extracted from Landing Zone (Populated by Python)
    SELECT * FROM dbo_stg_RawCustomers
)
,
lookup_0 AS (
    SELECT SegmentCode, SegmentName AS CustomerSegment FROM [dbo].[CustomerSegments]
)

,
transformed AS (
    SELECT
        lookup_0.CustomerSegment AS CustomerSegment,
        lookup_0.SegmentCode AS SegmentCode,
        source_data.CustomerId AS CustomerId,
        source_data.FullName AS FullName,
        source_data.EmailAddress AS EmailAddress,
        source_data.AccountBalance AS AccountBalance
    FROM source_data
    LEFT JOIN lookup_0 ON source_data.SegmentCode = lookup_0.SegmentCode
)

SELECT * FROM transformed
