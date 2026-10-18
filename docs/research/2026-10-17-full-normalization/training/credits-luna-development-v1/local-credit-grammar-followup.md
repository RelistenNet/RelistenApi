# Local credit grammar follow-up

The first matched-field comparison exposes three areas for local parser work: name boundaries, coordinated role clauses, and attribution within processing chains. The Luna v2 comparison uses the already-built local v1 parser, preserving its baseline. Local parser v2 implements only suffix-aware name-list splitting and explicit-role actor ownership. The solution builds and all 114 focused acquisition, extraction and cache checks pass. The observations below are development findings against draft labels, not adjudicated errors or a release accuracy estimate.

The v1 report scores 85 resolved fields with 70 taper/transferrer assertions. Local extraction has 60 matching assertions, seven extra predictions and ten missed assertions, spread across ten fields. Exact name-and-role scoring counts a name-boundary difference as both an extra prediction and a miss. Those counts therefore do not mean seven unrelated people were invented and ten entirely different people were absent.

The original fields remain in the ignored comparison directory:

```text
local-dev/normalization-data/2026-10-17-cross-artist/training-development-credits-v1/luna-field-comparison-v1/
```

This audit read `paid-run/selection.json`, `paid-run/results.jsonl` and `paid-report/report.json`, then checked the current [parser](../../../../../RelistenApi/Services/Normalization/RecordingCreditExtractor.cs) and [draft labels](../credits-development-v1/labels.jsonl). It did not inspect held-out fields, change labels, run extraction or change acceptance policy.

## Name boundaries account for six extras and five misses

Three fields retain parenthetical wording that the drafts exclude:

- `56969924-a8e9-9efb-b4e8-b8405519bf21:taper`: `Justin Harrington (Soundman)` versus the draft name `Justin Harrington`.
- The same source's `transferrer` field: `Jon King (Archivist)` versus `Jon King`.
- `b14df0c5-8fad-650d-81df-bd88eca60656:description`: `Recorded and Mastered by Ricky Rage (RVS)` versus `Ricky Rage`.

`AddNames` removes parenthetical email addresses but otherwise preserves parentheses. `Soundman` and `Archivist` are role descriptions. The literal acronym `RVS` does not, by itself, establish whether the wording is an affiliation or an alias; the draft calls it an affiliation. Confirm that judgment before treating this as a general stripping rule. Parenthetical aliases elsewhere must remain part of credited wording. Removing every parenthetical would trade one boundary error for another.

The remaining two fields have separate causes:

- `ff133b30-f111-27bf-835d-b8ed1e08189d:taper` contains `George Hodges, Jr.`. `NameSeparator` splits every comma, producing `George Hodges` and `Jr.`. Protect recognized personal suffixes during list splitting. Keep commas that separate genuinely different names working; retain suffix punctuation and the original evidence.
- `250fe15a-1f2e-a27f-3114-110a3e19631e:source` contains `Recorded by: JoE BohnerT - a Bird-Eye recor........DING!`. The role expression consumes the recording tagline as part of the name. A name-boundary parser should recognize a following descriptive clause while retaining exact spelling. Do not fix this by trimming every dash: hyphenated names and attached aliases are valid. This unusual punctuation needs more independent examples before a safe local rule is selected.

## Role clauses and a model-shaped handle account for five misses

- `f6bf2b0f-b373-0c40-c191-831a0ae9da6e:taper_notes` explicitly says `Taper: Farve4`. The parser reaches the name, then `Equipment` rejects the generic letters-plus-digit shape. That shape is weak evidence of equipment when an explicit person-credit label is present. Separate that weak cue from known equipment names, units and chains. Retain a credited handle without inventing a global identity; continue rejecting actual equipment in role fields. Do not whitelist this handle or accept every alphanumeric product token.
- `1142ea18-1e63-8dc4-dc23-e84e845fad1e:description` says `Recorded & upped to Dime by gf July 29, 2009`. The role expression cannot carry `Recorded` across the ampersand, upload wording and destination to the shared `by`. A coordinated-role parser should retain each supported operation and bound the actor before the trailing date. An upload operation must neither erase an explicit recording role nor create one on its own.
- `579274a3-7ab2-0a93-b3d3-7b86fd7bdb13:description` says `Transferred, mastered, and uploaded by Evan DePuy and Max Gilbraith`. The expression starts at `uploaded by`, so both names receive that role but lose the transfer role. Comma-separated and Oxford-comma operation lists should share the actor list, while preserving each operation's own role. The existing whitespace-only `and` grammar already handles simpler combinations.
- `77a09858-c858-c73c-3f98-43d653c2b26f:lineage` says `Soundboard Recording, mixed, and transferred by Josh Myers.` Local extraction returns the supported transfer role; the draft also assigns the disputed taper role. This label needs adjudication before a grammar change. The sentence can describe an existing soundboard recording that Josh mixed and transferred, without asserting that he recorded it. Do not turn every preceding noun `Recording` into a recording credit. The [machine-readable finding](local-credit-grammar-findings.jsonl) preserves the exact field hash and quotation without changing the frozen label. Keep v1/v2 paired inputs identical, then report any adjudicated label revision and its effect on the comparison explicitly.

## Chain attribution accounts for one extra transfer role

`f67f5854-ecd1-a861-02fb-f13c92123935:lineage` contains `CD > Soundforge 6 > USB > Adobe Audition > Flac > TLH Recorded By John Rivers`. Explicit extraction correctly finds the taper. `ExtractOperationActors` also sees a preceding `>` anywhere in the clause and assigns the same actor a transfer role. The chain describes processing but does not credit that processing to John.

The smallest correction is to keep an explicit `Recorded By` actor from being attributed to an earlier chain solely because that chain is nearby. Resolve the actor's own operation clause before applying the generic chain heuristic. Preserve actual operation credits such as `A > CD by Mark Cohen, EAC > SHN by Joe Jupille`, already covered by `NamedConversionAndTaggingOperationsKeepTheirDifferentRoles`. Evidence must support the particular role, not merely occur on the same line.

## Implement in narrow increments

The `recording-credits-v2` source implements the first two changes. A comma introducing `Jr`, `Sr`, `II`, `III`, `IV` or `V`, with optional literal period, stays inside its name when the suffix ends that list entry. Prose extraction preserves that suffix punctuation. Generic conversion inference now defers when an explicit role clause owns the same `by` actor, so a preceding chain cannot add a transfer role. Explicit negation remains owned by that clause too. The shared HTML/Unicode evidence reader is unchanged.

Public extraction regressions cover suffixes beside ordinary name lists and aliases, suffix punctuation in Unicode/HTML prose, earlier chains beside recording and mastering credits, negated recording, and separate conversion/tagging actors. These cases use different names and equivalent syntax. The key-free rerun of all 100 original fields changes exactly the suffix and chain-attribution cases. Against the unchanged draft denominator, local extraction recovers 61 of 70 priority-role assertions with four extra predictions and nine misses, exactly matching 77 of 85 resolved fields. The two fields improve without a new local error. The [versioned receipt](local-parser-v2.json) retains original input, source, loaded assembly and output hashes; cached Luna outputs and the 192-attempt spend ledger remain unchanged.

Coordinated role lists, credited handles versus equipment, tagline boundaries and parenthetical qualifiers remain separate work. Keep unknown descriptors and ambiguous noun phrases unresolved while additional development examples receive independent review. Affiliation/name boundaries need their own examples; they should not be hidden inside a broad cleanup expression.

Verify through `RecordingCreditExtractor.Extract` with different names and equivalent syntax, not assertions about regex text. Each positive case needs a nearby counterexample: suffix versus name list, recording versus conversion actor, role list versus musician roster, credited handle versus equipment, role qualifier versus personal alias, and literal negation. Check original Unicode evidence and roles together. Rerun the complete development comparison after a versioned parser change and report regressions as well as improvements; reserve untouched evaluation partitions for the later independent measurement.

The 15 uncertain fields remain outside false-positive and false-negative denominators. This follow-up does not resolve global people, change the label bundle or authorize automatic acceptance.

## Input fingerprints

- Selection SHA-256: `e550d7a3bfe77ebb8577117b577a8360d7daf43103777b6318af6ff11f312eb0`.
- Results SHA-256: `0c57812b151607baa05623d8f05a1380ea60623fde7b6d41fcf3e1f1c8d259e8`.
- Report SHA-256: `5c9c0fcc8af2317709ec76beb7c7a5d48ac76c4ae389e960035cc1e5e912b279`.
