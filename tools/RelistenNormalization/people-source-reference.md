# Selecting actors from source contact/profile references

`PeopleSourceContextProjection.ProjectReference` selects source-local actor candidates from a contact or URL's exact original citation. It does not accept an adjudicated name or winner. The existing named-mention API, its versions and baseline mode are unchanged.

The caller supplies the original evidence ID, authenticated artist UUID and a `PeopleSourceReferenceSelector`. The selector contains `Contact` or `Profile` plus `NormalizationTextEvidence`: original field name, field-local JSON pointer, raw quote and zero-based Unicode code-point offsets. `source_contact_reference` endpoints map to `Contact`; `source_url_reference` endpoints map to `Profile`. A URL requests inspection of profile attachment, not a qualified identity assertion.

The projector already extracts complete original fields through `RecordingCreditExtractor`. Selection first reproduces the raw span, then inspects the exact first contact/profile evidence citation. It does not search by normalized key, URL equality, proximity or adjudication. Joint recording roles collapse to one actor reference; independently expressed actors remain separate. Identify a source reference by `(evidenceId, field, ReferenceUuid)`. Identical original fields in different sources can produce the same local UUID; the naked UUID is never a canonical person ID.

## Attachment and uncertainty

A supported attached contact/profile produces `Matched` and the original actor facts. The candidate uses the existing `PeopleActorProjection`; contacts/profiles remain nonunique comparison hints, and canonical identifiers stay absent.

An `unknown_profile_namespace` issue can still prove attachment to one named source actor. The projector recognizes only the exact actor-specific unresolved fact emitted by the supported extractor. Its candidate has attachment `UnknownProfileNamespace`; the original URL and issue survive, but no provider namespace, profile key or identity is constructed.

Generic `unattributed_contact` and `unattributed_profile` facts retain their original candidate-reference hypotheses. One such hypothesis returns `Unattributed`, not `Matched`. Several references return `Ambiguous`. Those candidates keep the original actor facts and acquire no owned contact/profile hint. A caller must inspect status and attachment; candidate count alone does not establish attribution. `NotFound` covers a missing field, altered citation or reference the extractor cannot attach or retain as a hypothesis.

The serialized selector codes are `contact` and `profile`; statuses are `matched`, `ambiguous`, `unattributed` and `not_found`. Attachment codes are `contact`, `profile`, `unknown_profile_namespace` and `unattributed`. `ReferenceVersion` is `people-source-reference-v1`. Enum attributes preserve these codes independently of a serializer naming policy.

## Responsibility and verification

The pure projector validates coordinates against the supplied original field. The bundle adapter remains responsible for authenticating source bytes, reservation eligibility, field hashes, artist ownership and complete candidate pools before constructing it. The projector does not fetch input, read labels, choose a canonical identity, activate policy or write accepted facts.

The new public contract fixture covers confirmed contact/profile attachment, unknown namespaces, shared and nearby unowned hints, original Unicode/HTML spans, altered spans, wrong reference kinds and joint-role deduplication. Root owns compilation and runtime verification. The separate newer review-bundle adapter/export remains subsequent work; no broader CLI or baseline format is introduced here.
