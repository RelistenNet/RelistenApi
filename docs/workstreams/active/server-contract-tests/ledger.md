# Ledger: Server Contract Tests And Hardening

## Preregistered Experiments

### CT-001: Foundation Contract Checks

- Timestamp: 2026-06-20T04:43:12Z
- Intention / hypothesis: A small behavioral contract fixture can catch operational drift without brittle route reflection or migration-string tests.
- Responsible agent: root Codex agent.
- Start commit: `535f875`
- Worktree or branch: branch `codex/user-library-contract-hardening` in `/Users/alecgorge/code/relisten/RelistenApi`.
- Mutable surface: contract tests and small production fixes required by those tests.
- Validator: targeted `UserLibraryContractTests`, full `RelistenUserApiTests`, existing `RelistenApiTests`, `dotnet build RelistenApi.sln`, `git diff --check`, and secret-path scan.
- Expected deliverable: representative no-store checks for authenticated user-library reads plus real database schema-placement coverage for user-owned tables.
- Expected artifacts: Code diff, targeted/full test output, root AutoPlan board update, and this ledger outcome.
- Linked ExecPlan: none yet.

## Outcomes

### CT-001: Outcome

- Timestamp: 2026-06-20T04:43:12Z
- Result: Added `UserLibraryContractTests` covering no-store defaults across representative authenticated read endpoints and real Postgres placement checks for user-owned tables.
- Artifact locations: `RelistenUserApiTests/UserLibraryContractTests.cs`.
- Evidence: focused `UserLibraryContractTests` passed 8 tests; full `RelistenUserApiTests` passed 77 tests; existing `RelistenApiTests` passed 47 tests; `dotnet build RelistenApi.sln` passed with 0 warnings/errors; `git diff --check` passed; secret-path scan found no local OAuth secrets or paths.
- Review: Initial root review chose behavioral header/schema checks rather than brittle route reflection or migration-string assertions. Reviewer found no code/test correctness issues and flagged stale docs; the evidence and board text now match the completed checks.
- Conclusion: CT-001 covers the first hardening baseline for authenticated no-store defaults and user-data schema placement.
- next_action: done
