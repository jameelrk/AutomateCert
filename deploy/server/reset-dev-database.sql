-- DEVELOPMENT ONLY: drops the dev database; it is recreated on the next F5.
IF DB_ID(N'AutomateCert_Dev') IS NOT NULL
BEGIN
    ALTER DATABASE [AutomateCert_Dev] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [AutomateCert_Dev];
END
GO
