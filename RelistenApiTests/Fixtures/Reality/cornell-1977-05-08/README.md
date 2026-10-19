# Cornell 1977-05-08, v1 fixture

The Rust proof of concept's output (`out/reality.json`, `out/comments.json`) converted to the v1
contracts by its `v1-export` command (`src/v1.rs`; the Rust code was removed after commit
`5889870`). Known-good test data: the C# alignment is checked for agreement with and
improvement on it, not exact reproduction (alignment-spec.md §9).

| File | Contract |
|---|---|
| `reality.binpb` | `relisten.reality.v1.Reality` (proto/relisten/reality/v1/reality.proto) |
| `reality.pb.json` | The same, as protobuf-JSON, for reading and diffing (written by the contract tests) |
| `reality-inputs.json` | `relisten.reality-inputs/1`; its hash is `reality.inputs_hash` |
| `comments.json` | One comment; its anchors are `relisten.anchor/1` (the wrapper is not a contract) |
| `algo_params.json` | `media.algo_params` rows for the two `params_hash` values used |

**Ids.** Source and track uuids are Relisten's real ones (from `data/show.json`). The event,
performance and comment uuids don't exist yet anywhere, so they are uuid-v7-shaped values
derived deterministically from the PoC ids (`fixture_uuid7` in `src/v1.rs`). Event:
`01a14202-2800-7292-b696-2ae15226d1c8`, reality version 1.

**Algorithm versions** are `0.3.0-poc` and the params are the PoC's constants
(`rust-poc@e5ad022`). The PoC has one feature `VERSION = 3`, recorded as 3 for every component.

**Edited at WS-0 ratification (2026-10-17),** by one-off scripts through the generated C#
types, not by the converter: the reference layout became the spine (data-model.md §3.1: the
disc-split repeats at d1t09/d2t01 skipped with `file_start_us`, 1 µs rounding seams absorbed),
`reference.layout` and the per-file `track` uuids left `reality-inputs.json` and each file
gained its `title` (so `inputs_hash` changed), every `SourceFile` got its manifest `position` (and each reference file its `reference_start_us`, the pre-spine layout start), and the two DTS
sources got `orphan = true` from the rule in alignment-spec.md §5. No other value changed.

**Zeros:** the PoC writes some zeros as `-0.0`; the converter normalises them to `0`
(docs/formats.md).

Check with `dotnet test tests/contracts`. Set `RELISTEN_TEST_PG` to an empty Postgres to also
load it into the draft tables.
