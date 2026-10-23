# Contributing

1. Create a branch from `main`.
2. Keep the UI free of inline script/style (strict CSP) and escape all server text with `esc()`.
3. Database changes: update the entity **and** add an idempotent statement to `SchemaUpgrader.cs` and `deploy/server/upgrade-schema.sql`. Never put `{` or `}` in SQL strings passed to `ExecuteSqlRaw`.
4. New deployment target: add it to `DeploymentTemplates` in `src/AutomateCert.Shared/Contracts.cs`, add `src/AutomateCert.Agent/Scripts/<key>.ps1`, and add a diagnostics rule (see `docs/ARCHITECTURE.md`).
5. Run `deploy\verify-build.ps1` (0 errors) and `deploy\check-secrets.ps1` (no findings).
6. Update `CHANGELOG.md` and open a pull request.

Never commit credentials, customer data, real host names or e-mail addresses. Use `example.com` / `contoso.com` in docs and tests.
