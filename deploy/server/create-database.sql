-- OPTIONAL: the installer creates the database and login automatically. Use only if your DBA must pre-create them.
-- Local SQL Server: [NT SERVICE\AutomateCertServer]; remote SQL Server: the AutomateCert computer account, e.g. [CONTOSO\AUTOMATECERT01$].
IF DB_ID(N'AutomateCert') IS NULL CREATE DATABASE [AutomateCert];
GO
IF SUSER_ID(N'NT SERVICE\AutomateCertServer') IS NULL CREATE LOGIN [NT SERVICE\AutomateCertServer] FROM WINDOWS;
GO
USE [AutomateCert];
IF USER_ID(N'NT SERVICE\AutomateCertServer') IS NULL CREATE USER [NT SERVICE\AutomateCertServer] FOR LOGIN [NT SERVICE\AutomateCertServer];
ALTER ROLE db_datareader ADD MEMBER [NT SERVICE\AutomateCertServer];
ALTER ROLE db_datawriter ADD MEMBER [NT SERVICE\AutomateCertServer];
GO
