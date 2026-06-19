# Ledger: Auth And Sessions

## Preregistered Experiments

### AUTH-001: Provider Subject And Refresh Rotation

- Timestamp: 2026-06-19T20:35:14Z
- Intention / hypothesis: Provider-subject account creation and refresh-token rotation can be implemented with test fakes before live Apple/Google provider verification.
- Responsible agent: unassigned
- Start commit: `70747d7`
- Worktree or branch: none yet; recommended branch `codex/user-library-auth`
- Mutable surface: auth/profile models, auth services, auth/user controllers, auth migrations, and auth/session tests.
- Validator: targeted `UserLibraryAuth`/`RefreshToken` tests plus `dotnet build RelistenApi.sln`.
- Expected deliverable: Account creation by provider subject, rotating refresh-token storage, session listing/revocation, and tests proving reuse/revocation behavior.
- Expected artifacts: Code diff, targeted test output, root AutoPlan board update, and this ledger outcome.
- Linked ExecPlan: none yet.

## Outcomes

No outcome yet. This workstream is backlog until the foundation stream lands.

