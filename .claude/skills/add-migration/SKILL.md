---
name: add-migration
description: Add a HandyNet EF Core migration safely, including RLS policies and grants for new tenant-owned tables. Use whenever the EF model or database objects change.
---

1. Change the model in the module's `Infrastructure/*Model.cs` (tables only in the module's own schema).
2. From `backend/`: `dotnet ef migrations add <PascalCaseName> -p src/HandyNet.Migrations -s src/HandyNet.Migrations -o Migrations`
3. Review the generated `Up`/`Down`. Destructive operations (drop/rename column, type narrowing) must follow
   expand → migrate data → contract across releases; never drop financial or history data.
4. For every NEW tenant-owned table add, in the same or a follow-up migration (raw SQL via `migrationBuilder.Sql`):
   - `ALTER TABLE … ENABLE ROW LEVEL SECURITY;` and a policy using `infra.current_org()`;
   - grants are covered by default privileges, but revoke `UPDATE`/`DELETE` explicitly for append-only tables;
   - a matching `Down`.
5. Never edit a migration that has been applied anywhere (check `infra.__ef_migrations_history`).
6. Apply locally: `dotnet run --project src/HandyNet.Api -- migrate`, then run the `run-tests` skill.
7. Add an integration test in `DatabaseSecurityTests` for new RLS policies.
