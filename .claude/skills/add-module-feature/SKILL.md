---
name: add-module-feature
description: Checklist for adding a feature or a new module to the HandyNet modular monolith (folders, contracts, registration, endpoints, permissions, tests, docs).
---

Structure inside `backend/src/Modules/HandyNet.Modules.<Name>/`:
- `Domain/` entities and rules (no infrastructure dependencies), raising domain events for the outbox.
- `Application/` services, request/DTO records, FluentValidation validators (internal).
- `Infrastructure/<Name>Model.cs` implementing `IModuleModel` (public), own schema.
- `Contracts/` public interfaces/DTOs for other modules.
- `<Name>Module.cs` with `Add<Name>Module` and `Map<Name>Endpoints`.

Checklist:
1. New module: add project, references only in allowed direction (docs/architecture.md), register in
   `Program.cs` and `DatabaseMigrator.AllModels`, add a `PlatformModule` value if admins should toggle it, and
   add dependency rules to `ModuleBoundaryTests`.
2. Endpoints: `RequireModule` + `RequirePermission`; new permissions go into `Permissions` and `ForRole`.
3. Persistence: follow the `add-migration` skill (RLS for tenant data).
4. Tests: unit tests for domain rules, integration test for the workflow, mutation-check new guarantees.
5. Docs: update docs/architecture.md (map + table), docs/data-model.md, docs/roadmap.md; add an ADR for
   significant decisions.
