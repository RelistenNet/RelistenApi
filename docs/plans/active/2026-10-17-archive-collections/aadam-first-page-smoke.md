# Aadam: one configured discovery page, without publication

The actual `aadamjacobs` selector passed a bounded local discovery on October 17, 2026. The normal worker CLI started one page, retained and observed 100 Archive items, and returned `Limited=true`, `Completed=false`, with zero failures. All 100 items remained unresolved against the small local catalog. No artists or recordings were created, and intake ownership stayed unchanged.

The [sanitized evidence](evidence/aadam-first-page-smoke.json) retains the configured query, selected identifiers and membership checks, observation digests, workflow execution/result, checkpoint and HTTP checks. This closes the live configured-first-page check in the [collections plan](README.md); it does not validate a complete collection traversal.

## What ran

The service helper created an **unpublished local fixture**, `aadam-selector-smoke`, with an enabled Archive selector for `aadamjacobs`. It used `CollectionService` and did not create an artist/provider collection mapping. The isolated environment was the existing Jerry smoke database `relisten_jerry_20261017`, Temporal 17236 and Redis 16383.

The current standalone worker CLI admitted:

```text
--collection-discover=b8cd0b8c-7480-4af3-881b-6cc231022a01 --max-pages=1
```

A separate bounded read captured the exact first-page query used by the client:

```text
collection:aadamjacobs AND mediatype:audio
fields=identifier,collection,mediatype
count=100
```

All 100 returned rows identified audio items inside `aadamjacobs`. Their identifier set matched the workflow’s retained membership set. The workflow history contains exactly **one `ArchiveCollections.FetchPage` activity and 100 `ArchiveCollections.ObserveItem` activities**. Each observation retained the selected current metadata projection; together the projections contain 6,091 file entries. That count includes file representations and supporting files, not 6,091 songs or independent recordings.

The result was 100 discovered, 100 observed, zero linked, 100 unresolved and zero failed. “Unresolved” means no qualified canonical source was linked in this deliberately small local database. It is not a finding that the artists or recordings are absent from production, or that their performer identities cannot be established.

## Durable and public state

The selector remains `running`, with 100 discovered identifiers, a retained continuation cursor, zero pending items, `search_complete=false`, no completion timestamp and no error. That is the expected checkpoint for a successful page-limited execution. The next page was **not requested**, and the scan was not resumed.

Full before/after JSON snapshots matched for eight artists, 57 sources, 73 sets, 1,059 tracks, nine artist/provider mappings, three intake artist candidates and three intake ownership rows. The comparison includes existing playback URLs and catalog references. Discovery added collection membership, metadata observations and resolution state without changing canonical catalog rows or adopting an importer’s ownership.

The actual API returned **404** for the fixture’s overview, artists and recordings, by both slug and UUID. The collection list omitted the fixture. These seven HTTP checks confirmed it remained unpublished.

The discovery worker was stopped after verification. No schedules were registered. Existing Mule and Pavement services were preserved. Complete raw first-page and workflow evidence remains in `/tmp/relisten-jerry-20261017/aadam-first-page`; repository evidence omits raw metadata bodies, unrelated collection labels and credentials.

This check establishes the configured first-page request, per-row collection validation, retained evidence, bounded workflow result, durable checkpoint and absence of catalog writes. Continuation-page size, cursor expiry and terminal traversal remain separate checks; no full Aadam publication or production rollout is claimed.
