-- =============================================================================
--  Tequilas' Tavern — license activation schema (Azure SQL)
-- =============================================================================
--  Run once against your database (Azure Portal "Query editor", SSMS, or
--  `sqlcmd -S <server>.database.windows.net -d <db> -U <user> -P <pw> -i schema.sql`).
--
--  Keys are RSA-signed offline by the keygen tool and self-register here on their
--  FIRST activation, so you don't insert anything when you sell — the API fills
--  these tables in. Revoke a leaked key by setting Licenses.IsRevoked = 1.
-- =============================================================================

IF OBJECT_ID('dbo.Licenses', 'U') IS NULL
CREATE TABLE dbo.Licenses
(
    KeyId          CHAR(64)       NOT NULL PRIMARY KEY,   -- SHA-256 (hex) of the key token; the raw key is never stored
    Name           NVARCHAR(200)  NULL,
    Email          NVARCHAR(200)  NULL,
    OrderRef       NVARCHAR(100)  NULL,
    MaxActivations INT            NOT NULL DEFAULT 2,      -- from the signed key payload
    IsRevoked      BIT            NOT NULL DEFAULT 0,      -- set to 1 to kill a leaked key
    IssuedUtc      DATETIME2(0)   NOT NULL,               -- from the signed key payload
    FirstSeenUtc   DATETIME2(0)   NOT NULL DEFAULT SYSUTCDATETIME(),
    Notes          NVARCHAR(400)  NULL
);
GO

IF OBJECT_ID('dbo.Activations', 'U') IS NULL
CREATE TABLE dbo.Activations
(
    Id           BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    KeyId        CHAR(64)      NOT NULL REFERENCES dbo.Licenses(KeyId),
    HardwareId   CHAR(32)      NOT NULL,                  -- app's hashed machine fingerprint
    MachineName  NVARCHAR(200) NULL,
    ActivatedUtc DATETIME2(0)  NOT NULL DEFAULT SYSUTCDATETIME(),
    LastSeenUtc  DATETIME2(0)  NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_Key_Hardware UNIQUE (KeyId, HardwareId) -- one row per (key, machine); re-activation is idempotent
);
GO

-- Handy queries for you:
--   Customers/sales:   SELECT * FROM dbo.Licenses ORDER BY FirstSeenUtc DESC;
--   Where a key runs:  SELECT a.* FROM dbo.Activations a JOIN dbo.Licenses l ON l.KeyId = a.KeyId WHERE l.Email = 'buyer@x.com';
--   Revoke a key:      UPDATE dbo.Licenses SET IsRevoked = 1 WHERE Email = 'buyer@x.com';
