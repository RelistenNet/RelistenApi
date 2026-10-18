# Native provider people and recording credits

The shadow runner extracts people and recording roles from retained provider
inputs. It uses the provider decoder and retained credit extractor used by the
normalization pipeline. These outputs are proposals for inspection. They do not
publish identities, link people across recordings, or change playback ownership.

Supply admitted Archive and Phish inputs through the existing retained HTTP path,
and Local inputs through [joint root/directory admission](retained-local-shadow-inputs.md).
The same route accepts other supported provider representations when their
decoder produces recording observations. Venue inventories, setlist performers,
and a Local root by itself do not become recording credit accounts.

## Output and evidence

`ShadowNativePeopleAdapter` emits `people` and `credits` results for each
`provider_input`. People retain their names, aliases, typed actor facts, projected
matching facts, and original evidence. Credits retain the observed role and role
wording. Both preserve the original provider recording origin; file credits also
preserve their file origin. A file credit does not become a claim about the whole
recording.

`InputRevisionUuid` identifies the retained document that supports the assertion.
It never identifies a catalog source. Local root notes cite the root revision and
its selected account; directory and file notes cite the directory revision. JSON
pointers and text spans still address the original payload, including array
positions and Unicode text. Unknown credit wording remains a cited unresolved
assertion.

Role-free alias extraction runs over the same original recording, file, and
selected Local root fields. The `people` proposal retains
`SourceActorAliases`: each original scope contains its assertions, unresolved
statements, and parser coverage. For example, this roster can supply a named actor
and alias without creating a taper or transfer credit:

```text
Personnel:
Zoë aka Lark - vocals
```

Roster wording supplies interpretation context; it does not establish attendance.

These actors carry `SourceActorAlias` and the existing source-alias matching
projection. Their kind and credited actor facts remain absent. Repeated fields
retain separate local actor references, even when their text is identical. A
heading in recording prose cannot supply context for an alias in a separate file.
Coverage describes only the parser's handling of that original field; it does not
describe every alias the person may use. Alias-only actors add name retrieval
keys to `people` and leave role-bearing `credits` unchanged.

Catalog source, show, and track UUIDs are optional historical associations. The
shared `ShadowProviderObservations` reader exposes them only when the admitted
binding agrees with its retained input and catalog context. Local additionally
requires its exact root/directory pair, unique root account, captured artist
mapping, and exact playable URL anchors. A directory cannot borrow adjacent root
notes. Missing catalog support leaves the native actor inspectable with a binding
reason. Input revision UUIDs never substitute for missing catalog UUIDs.

Entity keys support retrieval: `source:<uuid>` and `show:<uuid>` use those
historical associations, while `person_name` keys use the existing normalized name
retrieval rule. Equal names are useful search neighbors; they do not establish
that two actors are one person. Account actor keys and all native outputs carry no
automatic acceptance authority.

## Partial results and replay

The runner observes supporting Local root rows during its preparation pass. The
people and venue adapters share one reader, so observing the same valid root
twice is harmless. A malformed or conflicting root invalidates only its own
directory support. Those directories still expose their own credits, and unrelated
provider inputs remain usable. A fresh replay with the original valid pair can
recover the root support.

Malformed primary captures produce explicit failed domain results. Unsupported
capture versions also fail explicitly. A decoded recording with no people remains
unresolved; a representation with no recording account is not applicable. Decode
issues remain visible even when valid sibling credits exist. For Local, selected
root issues retain their original root citations; issues belonging only to an
adjacent account stay outside the directory result.

`TestShadowNativePeopleAdapter` verifies native Archive arrays, file scope, Phish
envelope coordinates, optional catalog associations, exact Local account selection,
missing and malformed roots, recovery, and cited decode issues. Retained admission
tests verify the producers of the historical bindings. The actual retained wave
and reader checks are separate integration verification; this route makes no
freshness or current-catalog claim.
