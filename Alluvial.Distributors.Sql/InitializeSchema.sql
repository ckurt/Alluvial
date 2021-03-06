USE [{DatabaseName}]
GO

IF (NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'Alluvial')) 
BEGIN
    EXEC ('CREATE SCHEMA [Alluvial]')
END
GO

IF object_id('[Alluvial].[Tokens]') IS NULL
BEGIN
    CREATE SEQUENCE [Alluvial].[Tokens] 
     AS [int]
     START WITH 1
     INCREMENT BY 1
     MINVALUE 1
     MAXVALUE 2147483647
     CYCLE 
     CACHE 
END
GO

IF object_id('[Alluvial].[AcquireLease]') IS NULL
    exec('CREATE PROCEDURE [Alluvial].[AcquireLease] AS SELECT 1')
GO

ALTER PROCEDURE [Alluvial].[AcquireLease]
    @pool nvarchar(75),
    @waitIntervalMilliseconds int = 5000, 
    @leaseDurationMilliseconds int = 60000
    AS
    BEGIN

    SET NOCOUNT ON;
    SET TRANSACTION ISOLATION LEVEL SERIALIZABLE

    DECLARE @resourceName nvarchar(100)
    DECLARE @now datetimeoffset
    DECLARE @token int

    SELECT @token = NEXT VALUE FOR Tokens
    SELECT @now = SYSDATETIMEOFFSET()

	BEGIN TRAN
	SELECT TOP 1 @resourceName = ResourceName 
	FROM Leases WITH (XLOCK,ROWLOCK)
	WHERE 
		Pool = @pool
			AND 
		(Expires IS NULL OR Expires < @now) 
			AND 
		DATEADD(MILLISECOND, @waitIntervalMilliseconds, LastReleased) < @now 
		ORDER BY LastReleased;
    
    UPDATE Leases
        SET LastGranted = @now,
            Expires = DATEADD(MILLISECOND, @leaseDurationMilliseconds, @now),
            Token = @token
		output inserted.[ResourceName], 
		       inserted.[Pool],
			   deleted.[LastGranted],
			   inserted.[LastReleased],
			   inserted.[Expires],
			   inserted.[Token]
        WHERE 
            ResourceName = @resourceName
			AND 
			[Pool] = @pool

    COMMIT TRAN

    END


GO

IF object_id('[Alluvial].[ExtendLease]') IS NULL
    exec('CREATE PROCEDURE [Alluvial].[ExtendLease] AS SELECT 1')
GO

ALTER PROCEDURE [Alluvial].[ExtendLease]
    @resourceName nvarchar(100),
    @byMilliseconds int, 
    @token int  
    AS
    BEGIN

    SET NOCOUNT ON;
    SET TRANSACTION ISOLATION LEVEL SERIALIZABLE

    BEGIN TRAN

    DECLARE @expires datetimeoffset(7)

    SELECT @expires = 
    (SELECT Expires FROM Alluvial.Leases WITH (XLOCK,ROWLOCK)
        WHERE 
            ResourceName = @resourceName 
                AND 
            Token = @token)

    UPDATE Alluvial.Leases
        SET Expires = DATEADD(MILLISECOND, @byMilliseconds, @expires)
        WHERE 
            ResourceName = @resourceName 
                AND 
            Token = @token
                AND 
            Expires >= SYSDATETIMEOFFSET()

    SELECT Expires FROM Alluvial.Leases
        WHERE 
            ResourceName = @resourceName 
                AND 
            Token = @token

    IF @@ROWCOUNT = 0
        BEGIN
            ROLLBACK TRAN;
            THROW 50000, 'Lease could not be extended', 1;
        END
    ELSE
        COMMIT TRAN;
    END

GO

IF object_id('[Alluvial].[ReleaseLease]') IS NULL
    exec('CREATE PROCEDURE [Alluvial].[ReleaseLease] AS SELECT 1')
GO

ALTER PROCEDURE [Alluvial].[ReleaseLease]
    @resourceName nvarchar(100)  , 
    @token int  
    AS
    BEGIN
    SET NOCOUNT ON;

    DECLARE @now DATETIMEOFFSET(7)
    SELECT @now = SYSDATETIMEOFFSET()

    UPDATE Leases
    SET LastReleased = @now,
        Expires = null
    WHERE ResourceName = @resourceName 
    AND Token = @token

    SELECT LastReased = @now

    END

GO

IF object_id('[Alluvial].[Leases]') IS NULL
BEGIN
    CREATE TABLE [Alluvial].[Leases](
        [ResourceName] [nvarchar](100) NOT NULL,
        [Pool] [nvarchar](75) NOT NULL,
        [LastGranted] [datetimeoffset](7) NULL,
        [LastReleased] [datetimeoffset](7) NULL,
        [Expires] [datetimeoffset](7) NULL,
        [Token] [int] NULL,
     CONSTRAINT [PK_Leases_Pool] PRIMARY KEY CLUSTERED 
    (
        [ResourceName] ASC,
        [Pool] ASC
    )
    )

    CREATE NONCLUSTERED INDEX [IX_Leases.Token] ON [Alluvial].[Leases]
    (
        [Token] ASC
    )
END
GO
