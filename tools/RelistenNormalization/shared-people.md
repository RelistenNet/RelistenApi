# Complete shared-person support from retained source observations

`people-shared <completed-source-observations-run> <new-output-directory>` exports the full recording membership behind directly supported people hypotheses. It reads existing source observations and cached model interpretations. It makes no provider or model calls and changes no accepted catalog data.

The command pins the catalog envelope, credit and alias extractor versions. A track-title version change does not affect source actors and does not require another people backfill. The command reproduces local credit and alias facts from the six retained source fields under those declared extractor versions. It checks those facts and the resulting typed actors against the original output. Retained Luna credits keep their original cache basis and validated literal evidence; they cannot add actor contacts, profiles or kind. A changed contact key or uncertainty flag therefore cannot enter matching merely because the displayed person name stayed the same.

Matching uses the existing C# `IdentitySupport` policy: an exact name or explicit alias must agree alongside an attributed contact or profile, with complete actor projections and no contradictory facts. Neither a shared account nor equal names suffice. The export retrieves their intersection before comparison. This avoids comparing every pair of unrelated people who share an account. Conflicting or uncertain eligible comparisons remain explicit unsupported edges.

Equivalent identity facts have one bucket across all artists. Artist context does not establish or contradict global identity under this policy; each original occurrence retains its artist and the bucket reports the actual distinct artist and source counts. The export compares buckets once in each direction. It does not repeat a large comparison report for every recording or reduce membership to the eight examples in older diagnostic outputs. It constructs no transitive identity closure.

The output contains three gzip JSONL files and one manifest:

- `occurrences.jsonl.gz` contains every exact source actor, its current credit keys, bucket ID and original people run, chunk, physical line and hash. Role-free aliases have no credit keys. They acquire no recording role through grouping.
- `buckets.jsonl.gz` stores matching facts and representative citations once, with complete occurrence, source and artist counts. Its representative occurrence locates the original evidence.
- `hypotheses.jsonl.gz` stores directed bucket comparisons, their feature agreements and the existing policy's `supported`, `hasConflict`, `uncertain` and `accountAgrees` results. `acceptedIdentity` and `qualifiedForAutomaticAcceptance` remain false.

Consumers use only a `completed` manifest. It pins the source manifest, selected source chunks and all output files. The command checks the logical provider/source census and rechecks input hashes before completion. Latest replay outcomes determine each source; a latest failure prevents export. Existing target directories are refused. Failure or cancellation leaves an interrupted, unpublished directory; retry into a new target. Defaults cap output at 512 MiB and preserve 5.5 GiB of host free space.

The people directory must map current credit occurrences to retained credit row IDs using the refresh's exact witness correspondence. An unchanged row can retain an older or null credit key. Source UUID, displayed name or a new extraction key alone cannot prove that mapping. Keep the original row ID, run locator and evidence after mapping. Role-free source observations stay separate from credit membership.

Shared-person pages can then expand supported buckets to their complete mapped recording members through the existing `/api/shared-person` interface. Every membership keeps its direct C# support and original recording ownership. Production policy qualification remains a separate step.
