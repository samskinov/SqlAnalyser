-- Sample T-SQL script demonstrating what SqlAnalyzer can resolve.
-- Covers: VIEW reference, STORED PROC call, FUNCTION call,
--         explicit catalog qualifier, USE statement, GO batches.

USE MyDatabase
GO

-- A query that references a view in the current DB and a fully-qualified proc in another DB.
SELECT *
FROM dbo.vw_CustomerOrders       -- VIEW in MyDatabase.dbo
WHERE CustomerID IN (
    SELECT CustomerID
    FROM OCPR_EUC.dbo.vw_ActiveCustomers  -- VIEW in OCPR_EUC.dbo
)

GO

-- Call a stored procedure in the current database.
EXEC dbo.sp_RefreshCustomerStats @days = 30

GO

-- Call a scalar function.
SELECT dbo.fn_FormatName(FirstName, LastName) AS FullName
FROM dbo.vw_CustomerOrders

GO

USE OCPR_EUC
GO

-- Now everything unqualified resolves against OCPR_EUC.
SELECT TOP 100 *
FROM dbo.ST_SalesData             -- TABLE in OCPR_EUC.dbo (not documented but detected)
CROSS APPLY dbo.fn_GetSalesRegion(RegionCode) AS r  -- TVF in OCPR_EUC.dbo

GO
