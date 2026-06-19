# Ledger: Playback History

## Preregistered Experiments

### HIST-001: Batch Ingest Idempotency

- Timestamp: 2026-06-19T20:35:14Z
- Intention / hypothesis: A separate ingest-key table can provide race-safe dedupe for history batch uploads without relying on unsupported hypertable unique indexes.
- Responsible agent: unassigned
- Start commit: `70747d7`
- Worktree or branch: none yet; recommended branch `codex/user-library-history`
- Mutable surface: history models, services, controllers, migrations, and history tests.
- Validator: targeted playback-history ingest tests plus `dotnet build RelistenApi.sln`.
- Expected deliverable: Batch upload endpoint, ingest-key idempotency, playlist attribution persistence, and history-disabled behavior tests.
- Expected artifacts: Code diff, targeted test output, root AutoPlan board update, and this ledger outcome.
- Linked ExecPlan: likely needed before Timescale retention and continuous aggregate work.

## Outcomes

No outcome yet. This workstream is backlog until auth and settings are available.

