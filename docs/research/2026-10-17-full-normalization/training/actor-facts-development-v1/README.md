# Source actor facts: development annotations

This bundle supplies source-grounded expectations for extracting contributor names, aliases, contact references and profile claims from recording metadata. It covers **543 whole original fields from 207 recordings, 172 artists and 38 discovery hypotheses**. It can expose mistakes in local extraction and attachment. It does not establish global person identity or launch accuracy.

The implementer should start with `manifest.json`, validate the retained inputs, and load `field-index.jsonl` with the ignored original fields. Use `labels.jsonl` for field-local expectations and `citations.jsonl` to resolve their exact evidence. Public source values, contact details and long prose stay in ignored files; tracked references carry hashes rather than those values.

## What was selected

Selection includes every original field cited by an occurrence, structured credit or person-context citation in the frozen [people discovery receipt](../../scale/evidence/people-identity-discovery-v2.json). This gives 205 taper fields, 174 transferrer fields, 121 descriptions, 42 taper notes and one source field. Nothing was added from excluded artists, reserved seed cases or held-out families.

The source index authenticates both artist and recording-family development partitions against the frozen reservation index before selected original text is decoded. It then authenticates the copied sources against their exact physical rows in the original annotation evidence. Whole-file byte hashing does not decode excluded rows. The potentially exposed `source:3717752` family remains excluded. Known exposed `track:33689437` and `track:7018353` are track references, mapped through original metadata where present. The older 102-case seed remains a contract/regression resource; its labels do not establish launch accuracy.

Each selected whole field is a task. Different fields from one recording remain separate tasks, so a name in `taper` cannot silently resolve an unsigned statement in `description`. The original 168-question people bundle and the 38-family discovery inventory are unchanged.

## What a label means

A positive fact describes an expressed recording or transfer contributor and its local evidence. It does not authenticate ownership of an account, email address or website. An actor UUID identifies one expressed actor inside one field. It is not a canonical person ID, and equal names or contact values in other fields do not merge actors.

- **Aliases** require an explicit alias statement. A dash, slash, similar name or shared reference does not establish an alias.
- **Contacts** require a directly named attachment. Several names followed by several contacts remain unresolved unless the field states which belongs to which. A contact-only field does not supply a named actor.
- **Profiles** distinguish an attributed personal profile, a credited project profile and an explicit owner statement. Recording-resource links and promoted websites do not automatically become actor profiles.
- **Context** preserves the original named role or an explicit description. An unsigned first-person statement does not identify its speaker merely because another part of the field names a contributor.
- **Kind** is person or group only when the field expressly supports that interpretation. A plausible full name, handle or contact is insufficient.
- **Relationships** distinguish an explicit alias, joint roles for one actor and locally distinct coordinated actors. Local distinctness is not a cross-recording inequality claim.

Role mappings preserve domain meaning: recording/taping supports `Taper`, recording transfer supports `Transferrer`, matrix construction supports `MatrixBuilder`, mastering supports `MasteringEngineer`, and uploading/seeding supports `Uploader`. Processing, mixing, editing, tracking and a bare conversion label can lack a clear mapping. Their lexical evidence remains visible as unresolved context. Performer rosters, composition credits and unrelated promotion are outside positive contributor scope.

The frozen contract snapshots are receipts for the semantics used during annotation. They are not copied runtime implementations. The annotation author did not read extractor code, predictions or runtime results. A corpus-specific annotation aid applied a reviewed literal inventory and attribution templates; these are development drafts, not independent human adjudication.

## Counts and coverage

There are 535 field-local actors and 547 name references. The drafts contain 240 contact claims, 44 profile claims, 532 named-role context facts, 12 aliases, 90 joint-role relationships and seven locally distinct-actor relationships. All 12 alias facts repeat one alias pair across recordings. Repetitions are correlated evidence, not 12 independent identity examples.

There are **zero justified person/group positives** and **zero complete Kind fields**. This corpus does not measure person/group classification accuracy. Full names, source-local role attributions and external project pages were not used to invent kind labels.

Every task declares coverage separately for Aliases, Contacts, Profiles, Context, Kind and Relationships. Complete coverage is recorded for 331 Alias fields, 375 Contact fields, 380 Profile fields, 372 Context fields and 330 Relationship fields. No field is complete across all kinds. Long prose remains partial except the specifically recorded whole-field audit in the manifest. Twenty-five fields have no resolved actor.

Unknown or unlabeled facts are not negatives. For a partial kind, replay can check whether the labeled positive fact was found and attached correctly; extra output needs source review before it can count as false positive. Only complete coverage supports missing-fact negatives. Keep unresolved attachments and unmapped roles in the report instead of forcing them into a binary score. Initial replay should compare literal evidence and attachment; private lookup keys are annotation conventions, not verified identifiers.

## Evidence and data contracts

`field-index.jsonl` binds each task to its original evidence ID, JSON field path, text hash, code-point length and source partitions. `citations.jsonl` uses half-open Unicode code-point spans `[start, end)` over the **raw original field**, including markup. A citation carries the exact selected substring hash. Do not compute these offsets against rendered HTML, UTF-16 units or a normalized string.

`labels.jsonl` contains field-local actors, name references, positive facts, relationships, unresolved items, excluded resource links, per-kind coverage and pending adjudication. A fact cites its actor anchor and its reference/relationship evidence. Opaque `value:` references resolve only through the ignored `values.jsonl`. That file retains literal and lookup values with content hashes. Neither value hashes nor annotation IDs are model features.

The manifest pins all source copies, original annotation evidence, discovery/reservation inputs, semantic snapshots and frozen label files. The validator checks original source ownership, exact spans, field-local relationships, whole-field selection and uncertainty contracts. It does not decide whether the annotation author interpreted a source correctly.

Before any internal split, call `build_split_components` on **all 543 tasks**, including unresolved and actorless fields. Recording, retained recording-family and discovery-hypothesis links yield 38 connected components; the largest has 58 fields. Filter scored facts only after grouping. These groups prevent leakage between correlated development folds; they are not verified identities.

## Independent review

The independent reviewer receives one frozen 43-field packet containing original text and the two semantic contract snapshots. The packet covers all 38 discovery hypotheses plus five short input-form cases. It contains no draft facts, answers, rationales or runtime predictions. The reviewer previously saw seven discovery contexts, so this is an answer-blind development review, not untouched source evaluation.

First answers were frozen before comparison. The ignored originals and their receipt remain unchanged. `independent-review.jsonl` preserves the first answers with source prose and literal values replaced by hashes; `independent-review-receipt.json` pins the originals and explains the comparison. The reviewer also disclosed earlier recording-credit parser and Luna tooling work in this project, while reading no current actor-fact code or predictions during annotation.

The subset has 43 draft actor entries and 49 independently proposed entries; 40 primary anchor spans align exactly. Within those anchors, 10 role sets, two contact attachments and one profile claim differ. Nine fields have different primary anchor selections, and 32 have different completeness declarations. Repeated href/display copies of one profile literal are collapsed when comparing claim values. These are comparison results, not an error rate: differences can reflect evidence boundaries, treatment of unmapped processing roles or missing draft facts. The original answers, both coverage declarations and all disagreements remain visible; no labels were rewritten to agree, and none of these differences is adjudicated.

All primary labels remain development drafts with adjudication pending. The selection receipt pins both the packet and the draft label/citation hashes. A local replay must report disputed expectations separately before using them to set extraction policy.

## Verify and replay

From the repository root:

```sh
python3 -O docs/research/2026-10-17-full-normalization/training/actor-facts-development-v1/validate_bundle.py
python3 -O docs/research/2026-10-17-full-normalization/training/actor-facts-development-v1/test_validate_bundle.py
```

The validator needs the retained ignored originals at the manifest paths. Missing retained data is an explicit failure; it does not trigger a provider fetch. Tests exercise changed sources, held-out metadata rejection before source decoding, malformed raw Unicode spans under optimized Python, foreign-field evidence, relationship ownership, whole-field denominators and the difference between uncertainty and negative evidence. They also verify that unselected malformed UTF-8 rows are not decoded.

Runtime replay is separate work. It must use the frozen whole-field denominator, report complete and partial coverage separately, and identify independently reviewed or disputed expectations. No paid calls, database changes or production policy activation were performed to create this bundle.
