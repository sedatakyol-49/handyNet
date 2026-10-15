---
name: run-tests
description: Build and run all HandyNet backend tests (unit, integration on real PostgreSQL, architecture) and report results. Use after any backend change.
---

1. From `backend/`, run:
   `dotnet test HandyNet.slnx --artifacts-path ../.artifacts -nologo`
   (`--artifacts-path` avoids file locks when the API is running from `bin/`).
2. Integration tests need a PostgreSQL superuser connection: env `HANDYNET_TEST_PG_ADMIN`, or
   `backend/tests/HandyNet.IntegrationTests/testsettings.local.json`, or Docker. If none is available, say so —
   do not report integration tests as passed.
3. For a release build check also run `dotnet build HandyNet.slnx -c Release -warnaserror` and
   `dotnet ef migrations has-pending-model-changes -p src/HandyNet.Migrations -s src/HandyNet.Migrations`.
4. Report the exact Passed/Failed counts per test project and any failure messages.
5. If a .NET assembly fails to load with "An Application Control policy has blocked this file", Windows Smart App
   Control is on; tell the user instead of working around it.
