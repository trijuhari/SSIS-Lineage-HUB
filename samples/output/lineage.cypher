// SSIS Lineage Graph Import Script
// Schema: see docs/neo4j-schema.md in the repository.
// Run in Neo4j Browser or cypher-shell against an empty or dedicated database.
CREATE CONSTRAINT IF NOT EXISTS FOR (p:Package) REQUIRE p.id IS UNIQUE;
CREATE CONSTRAINT IF NOT EXISTS FOR (t:Task) REQUIRE t.id IS UNIQUE;
CREATE CONSTRAINT IF NOT EXISTS FOR (c:Component) REQUIRE c.id IS UNIQUE;

// Packages
MERGE (p:Package {id: 'CreateSalesForecastInput', name: 'CreateSalesForecastInput', path: '/home/hirazone/Documents/SSIS-Project-Documentation/samples/samples/Tutorial-Sample-1/CreateSalesForecastInput.dtsx'});

// Tasks
MERGE (t:Task {id: '{AD1BE492-BB24-47DF-B4CB-40527E800BC5}', name: 'Create Sales Forecast Input Spreadsheet', type: 'DTS.Pipeline.1'});

// Components
MERGE (c:Component {id: '1', name: 'OLE DB Source', type: 'Third-Party: OLE DB Source', sql: 'select\n  g.GeographyKey\n, g.SalesTerritoryKey  \n, g.City\n, g.StateProvinceName [State]\n, g.PostalCode\nfrom dbo.DimGeography g\njoin dbo.DimSalesTerritory t \n  on t.SalesTerritoryKey = g.SalesTerritoryKey\nwhere t.SalesTerritoryCountry = \'United States\' \norder by g.StateProvinceName, g.City'});
MERGE (c:Component {id: '16', name: 'Derived Column', type: 'Third-Party: Derived Column', sql: ''});
MERGE (c:Component {id: '22', name: 'Excel Destination', type: 'Third-Party: Excel Destination', sql: 'Excel Destination'});

// Task Parent-Child relationships
MATCH (t:Task {id: '{AD1BE492-BB24-47DF-B4CB-40527E800BC5}'}), (p:Package {id: 'CreateSalesForecastInput'}) MERGE (t)-[:BELONGS_TO]->(p);

// Component Parent-Child relationships
MATCH (c:Component {id: '1'}), (t:Task {id: '{AD1BE492-BB24-47DF-B4CB-40527E800BC5}'}) MERGE (c)-[:BELONGS_TO]->(t);
MATCH (c:Component {id: '16'}), (t:Task {id: '{AD1BE492-BB24-47DF-B4CB-40527E800BC5}'}) MERGE (c)-[:BELONGS_TO]->(t);
MATCH (c:Component {id: '22'}), (t:Task {id: '{AD1BE492-BB24-47DF-B4CB-40527E800BC5}'}) MERGE (c)-[:BELONGS_TO]->(t);

// Execution Flow

// Data Flow connections
MATCH (c1:Component {id: '1'}), (c2:Component {id: '16'}) MERGE (c1)-[:FLOWS_TO]->(c2);
MATCH (c1:Component {id: '16'}), (c2:Component {id: '22'}) MERGE (c1)-[:FLOWS_TO]->(c2);

// Column-level Lineage mappings
MATCH (c1:Component {id: '1'}), (c2:Component {id: '22'}) CREATE (c1)-[:MAPS_TO {srcCol: 'GeographyKey', destCol: 'GeographyKey', expr: '', opType: 'XML_FALLBACK'}]->(c2);
MATCH (c1:Component {id: '1'}), (c2:Component {id: '22'}) CREATE (c1)-[:MAPS_TO {srcCol: 'SalesTerritoryKey', destCol: 'SalesTerritoryKey', expr: '', opType: 'XML_FALLBACK'}]->(c2);
MATCH (c1:Component {id: '1'}), (c2:Component {id: '22'}) CREATE (c1)-[:MAPS_TO {srcCol: 'City', destCol: 'City', expr: '', opType: 'XML_FALLBACK'}]->(c2);
MATCH (c1:Component {id: '1'}), (c2:Component {id: '22'}) CREATE (c1)-[:MAPS_TO {srcCol: 'State', destCol: 'State', expr: '', opType: 'XML_FALLBACK'}]->(c2);
MATCH (c1:Component {id: '1'}), (c2:Component {id: '22'}) CREATE (c1)-[:MAPS_TO {srcCol: 'PostalCode', destCol: 'PostalCode', expr: '', opType: 'XML_FALLBACK'}]->(c2);
MATCH (c1:Component {id: '16'}), (c2:Component {id: '22'}) CREATE (c1)-[:MAPS_TO {srcCol: 'Forecast', destCol: 'Forecast', expr: '', opType: 'XML_FALLBACK'}]->(c2);
