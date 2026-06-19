# Ledger: User Library Server Foundation

## Preregistered Experiments

### FND-001: Minimal Separate User-Library API Foundation Slice

- Timestamp: 2026-06-19T20:35:14Z
- Intention / hypothesis: A small additive slice can establish a separate `RelistenUserApi` server boundary without changing existing catalog behavior.
- Responsible agent: root Codex agent.
- Start commit: `70747d7`
- Worktree or branch: branch `codex/user-library-foundation` in `/Users/alecgorge/code/relisten/RelistenApi`.
- Mutable surface: new `RelistenUserApi/` web project, optional tiny shared class-library project, solution file, migration bootstrap files, and `RelistenUserApiTests/` or `RelistenApiTests/` fixtures whose names contain `UserLibrary`.
- Validator: `dotnet sln RelistenApi.sln list`, targeted user-library tests, and `dotnet build RelistenApi.sln`.
- Expected deliverable: `RelistenUserApi` added to the solution, a minimal protected `/api/v3/library/users/me` or equivalent foundation endpoint in that separate server, snake-case DTO serialization tests, and schema-bootstrap/migration placement checks.
- Expected artifacts: Code diff, targeted test output, root AutoPlan progress update, and this ledger outcome.
- Linked ExecPlan: none initially. Create one only if the foundation slice grows beyond a single coherent implementation pass.

## Outcomes

### FND-001 Outcome

- Timestamp: 2026-06-19T21:02:30Z
- End commit: `b7cab47`
- Artifact location: `RelistenUserApi/`, `RelistenUserApiTests/`, and `RelistenApi.sln`.
- Evidence summary:
  - `dotnet sln RelistenApi.sln list` includes `RelistenUserApi/RelistenUserApi.csproj` and `RelistenUserApiTests/RelistenUserApiTests.csproj`.
  - `dotnet test RelistenUserApiTests/RelistenUserApiTests.csproj` passed: 7 tests.
  - `dotnet test RelistenApiTests/RelistenApiTests.csproj` passed: 47 tests.
  - `dotnet build RelistenApi.sln` passed; current output includes the existing nullable warning in `RelistenApi/Startup.cs` and 0 errors.
  - Runtime smoke: `dotnet run --project RelistenUserApi/RelistenUserApi.csproj --urls http://127.0.0.1:5119` started the separate process; `GET /health` returned HTTP 200 with body `OK`; unauthenticated `GET /api/v3/library/users/me` returned HTTP 401 with `Cache-Control: no-store`.
  - `git diff --check` passed.
- Conclusion: pass. The foundation slice creates a separate buildable/runnable `RelistenUserApi` project with a protected profile endpoint, disabled-by-default auth seam, test-only auth override, snake-case DTO serialization, no-store headers for `/api/v3/library`, and schema-qualified `user_data` bootstrap SQL.
- next_action: done
- Next move: promote `auth-and-sessions` when continuing the broader server AutoPlan.
