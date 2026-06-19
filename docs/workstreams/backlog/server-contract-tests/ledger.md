# Ledger: Server Contract Tests And Hardening

## Preregistered Experiments

### CT-001: Foundation Contract Checks

- Timestamp: 2026-06-19T20:35:14Z
- Intention / hypothesis: Early contract tests for serialization and schema placement will catch drift before larger endpoint families are implemented.
- Responsible agent: unassigned
- Start commit: `70747d7`
- Worktree or branch: none yet; recommended branch `codex/user-library-contract-tests`
- Mutable surface: contract tests and small production fixes required by those tests.
- Validator: targeted contract tests plus `dotnet build RelistenApi.sln`; eventually full `dotnet test RelistenApiTests/RelistenApiTests.csproj`.
- Expected deliverable: Snake-case DTO tests, route prefix assertions, cache/no-store checks for foundation endpoints, and migration placement assertions.
- Expected artifacts: Code diff, targeted/full test output, root AutoPlan board update, and this ledger outcome.
- Linked ExecPlan: none yet.

## Outcomes

No outcome yet. This workstream is backlog until at least one user-library endpoint exists.
