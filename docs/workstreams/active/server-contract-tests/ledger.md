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

### CT-002: Separate Deployment Artifact

- Timestamp: 2026-06-20T04:49:12Z
- Intention / hypothesis: The user API needs its own image build and rollout entrypoint so it can be deployed and scaled independently from the catalog API while sharing the solution.
- Responsible agent: root Codex agent.
- Start commit: `ebcbfb3`
- Worktree or branch: branch `codex/user-library-deployment-hardening` in `/Users/alecgorge/code/relisten/RelistenApi`.
- Mutable surface: Dockerfile/workflow/deploy helper, dockerignore, and workstream docs.
- Validator: `dotnet publish RelistenUserApi/RelistenUserApi.csproj -c Release --no-restore`, `dotnet build RelistenApi.sln`, `dotnet test RelistenUserApiTests/RelistenUserApiTests.csproj --filter UserLibraryContractTests`, `git diff --check`, and secret-path scan.
- Expected deliverable: A separate user API Dockerfile, manual image workflow, and deploy helper that do not affect the existing catalog image workflow.
- Expected artifacts: Code diff, validator output, root AutoPlan board update, and this ledger outcome.
- Linked ExecPlan: not needed unless Kubernetes manifests are added to this repository.

### CT-003: Account Export And Deletion Gates

- Timestamp: 2026-06-20T05:07:54Z
- Intention / hypothesis: Account export and deletion should be implemented as real authenticated user API behavior with tests that prove response shape, token invalidation, user-data deletion, and safe handling of shared playlist references.
- Responsible agent: root Codex agent.
- Start commit: `92bf849`
- Worktree or branch: branch `codex/user-library-account-gates` in `/Users/alecgorge/code/relisten/RelistenApi`.
- Mutable surface: users controller, account export/delete service/DTOs, behavior tests, and workstream docs.
- Validator: focused `UserLibraryAccountTests`, full `RelistenUserApiTests`, existing `RelistenApiTests`, `dotnet build RelistenApi.sln`, `git diff --check`, secret-path scan, and reviewer pass.
- Expected deliverable: Authenticated `/api/v3/library/users/me/export` and `DELETE /api/v3/library/users/me` endpoints, with high-signal behavior tests for snake-case export, scoped data export, account cleanup, stale token rejection, and shared playlist preservation.
- Expected artifacts: Code diff, targeted/full test output, root AutoPlan board update, and this ledger outcome.
- Linked ExecPlan: not needed unless provider recent-reauth work is added.

## Outcomes

### CT-001: Outcome

- Timestamp: 2026-06-20T04:43:12Z
- Result: Added `UserLibraryContractTests` covering no-store defaults across representative authenticated read endpoints and real Postgres placement checks for user-owned tables.
- Artifact locations: `RelistenUserApiTests/UserLibraryContractTests.cs`.
- Evidence: focused `UserLibraryContractTests` passed 8 tests; full `RelistenUserApiTests` passed 77 tests; existing `RelistenApiTests` passed 47 tests; `dotnet build RelistenApi.sln` passed with 0 warnings/errors; `git diff --check` passed; secret-path scan found no local OAuth secrets or paths.
- Review: Initial root review chose behavioral header/schema checks rather than brittle route reflection or migration-string assertions. Reviewer found no code/test correctness issues and flagged stale docs; the evidence and board text now match the completed checks.
- Conclusion: CT-001 covers the first hardening baseline for authenticated no-store defaults and user-data schema placement.
- next_action: done

### CT-002: Outcome

- Timestamp: 2026-06-20T04:49:12Z
- Result: Added `Dockerfile.userapi`, a separate manual GitHub Actions image workflow, a `deploy-user-api` helper, and `.dockerignore` entries for user API build output.
- Artifact locations: `Dockerfile.userapi`, `.github/workflows/build_and_push_user_api_image.yml`, `.dockerignore`, `deploy-user-api`, and `README.markdown`.
- Evidence: `dotnet publish RelistenUserApi/RelistenUserApi.csproj -c Release --no-restore -o /tmp/relisten-user-api-publish` passed and produced `RelistenUserApi.dll`; focused `UserLibraryContractTests` passed 8 tests; `dotnet build RelistenApi.sln` passed with 0 warnings/errors; `docker build -f Dockerfile.userapi --target runtime ... -t relisten-user-api:local .` passed; container smoke with `UserData__InitializeSchema=false` returned `health OK`; workflow YAML parse passed; `git diff --check` passed; secret-path scan found no local OAuth secrets or paths.
- Review: Root review tightened `.dockerignore` so user API/test build output and `.DS_Store` files do not enter Docker context. Reviewer findings on rollout success, mutable kubectl action refs, concurrent `latest` deploys, and preserving the existing base64 `KUBE_CONFIG` secret contract were fixed with deploy concurrency, a pinned Tailscale action SHA, pinned kubectl setup, base64-decoded kubeconfig setup, direct kubectl commands, and `kubectl rollout status --timeout=180s`. Reviewer verification found no deployment workflow blockers.
- Conclusion: CT-002 provides a separate deployable image path for the user API while leaving the catalog image workflow untouched.
- next_action: done

### CT-003: Outcome

- Timestamp: 2026-06-20T05:07:54Z
- Result: Added account export and deletion endpoints under `/api/v3/library/users/me`, backed by an account data service and account export DTOs.
- Artifact locations: `RelistenUserApi/Controllers/UsersController.cs`, `RelistenUserApi/Services/UserAccountDataService.cs`, `RelistenUserApi/Models/AccountDtos.cs`, and `RelistenUserApiTests/UserLibraryAccountTests.cs`.
- Evidence: focused `UserLibraryAccountTests` passed 2 tests; full `RelistenUserApiTests` passed 79 tests; existing `RelistenApiTests` passed 47 tests; `dotnet build RelistenApi.sln` passed with 0 warnings/errors; `git diff --check` passed; secret-path scan found no committed local Google OAuth client files, downloaded paths, or secret references.
- Review: Reviewer found a high-severity deletion-order bug for accounts with non-empty owned playlists because owned playlist entries/blocks still referenced the user through non-cascading FKs. The service now deletes owned playlists before deleting the user and the deletion test creates a non-empty owned playlist to lock in the fix. Validator confirmed the original finding was valid, the fix is sufficient, and no new actionable findings remain.
- Conclusion: CT-003 covers the M1 server-side account export/deletion gate without adding brittle migration-string tests.
- next_action: done
