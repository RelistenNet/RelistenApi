# Export actual people pair features

`people-pair-baseline` compares every declared candidate in the frozen development people bundle with the public C# matcher. It starts no host, database, model client or network activity. It exports a development mention-pair projection; it is not a replay of a populated canonical people catalog and permits no accepted identity changes.

```sh
dotnet tools/RelistenNormalization/bin/Debug/net10.0/RelistenNormalization.dll \
  people-pair-baseline "$repository_root" "$people_bundle_directory" \
  "$new_output_directory" "$frozen_bundle_manifest_sha256"
```

Root builds and executes this command after source review. The author has not run .NET. Supply the manifest hash from the frozen bundle receipt, not a new hash invented after modifying input. Output directories must be new. `manifest.json` is written last; partial output without it is not a completed baseline. Cancellation preserves any flushed prefix and does not invent another source or accepted result.

The loader first verifies bundle/input hashes and reservation metadata. Every selected source must match the eligible development artist/family metadata and avoid reserved sources/families. Only then does it select original annotation **physical byte lines** by authenticated line numbers and hashes, and decode those selected records. Other raw records, including held-out lines, are not decoded. Exact field SHA256 and Unicode code-point spans authenticate mention wording. Input snapshots are bounded to 64 MiB per file, 128 MiB combined and at most 32 declared tracked files. A concurrent file change before completion rejects the run.

Only the retained mention's exact name and source artist UUID enter `PersonMatchRecord`. Actor kind is null. Alias lists, contacts, profiles and external identifiers are absent. A label saying person, alias or same actor cannot provide a feature. Artist UUIDs establish source context overlap; they are not learned class IDs. Different artists do not prove different global identity. The matcher receives no canonical candidate UUID or active policy.

`people-pair-baseline-contract.json` freezes the exchange schema. Each row retains the complete actual `ComparePeople` result and its five numeric scorer inputs: token similarity, exact-name agreement, artist-context agreement, contact agreement and profile agreement. The last two are zero because this projection has no actor context. Their absence remains explicit in the report; zero is not a contradiction. Contact/profile/kind facts can be added only through a separately versioned, source-supported projection.

All declared pools are preserved, including unknown rivals. Credit expressions and unresolved references remain diagnostic rows with `projectionSupported=false`; they cannot silently become individual names or fitting examples. Labels, grouping keys and independent review answers never enter features. The exporter checks declared edge completeness against the frozen inventory; the Python experiment remains responsible for the existing bundle validator's full question/retrieval/grouping semantics and independently joining target labels.

Use `sameRecording`, not just the task header, for scope reporting. The current 43 cross-recording question pools include same-recording neighbors: their 13 same/0 different/61 insufficient edges include only 11 same/0 different/59 insufficient **true cross-recording** edges. These are correlated development drafts, not accuracy estimates. Counts are observed, not hardcoded in the command. Unknown actor kind remains unknown; this export supplies neither creation capability nor launch-policy authority.

The downstream experiment must rehash the bundle inputs, baseline output and executing assembly artifacts. It must group all 168 current tasks before filtering targets, including actor-kind and unknown bridges. Rule/logistic/tree comparisons use the same complete pools. Portable logistic parameters use raw logits in the existing linear scorer; tree outputs remain development diagnostics until a supported runtime scorer and independent evaluation justify their use.
