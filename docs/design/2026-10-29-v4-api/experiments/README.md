# Legacy ID encoding probe

Question: can a .NET codec preserve legacy 128-bit values while producing the
26-character suffix used by TypeID, without requiring a valid UUID version?

Result on 2026-10-29, .NET SDK 10.0.100: **yes for the tested codec**. All nine
published valid vectors and 10,000 arbitrary 128-bit values passed. This does
not qualify a production library or the API serialization contract.

The probe converts canonical UUID hex text to `UInt128`, encodes lowercase
Crockford base32, then decodes back to canonical UUID text. It does not alter
UUID version or variant bits. It leaves Switz's short IDs unchanged.

The known-vector check matters: a codec can pass its own round-trip test while
disagreeing with other implementations. C#'s default `Guid.ToByteArray()` gives
`33221100554477668899aabbccddeeff` for UUID
`00112233-4455-6677-8899-aabbccddeeff`, whereas the specified encoding follows
the canonical big-endian order. Production code must choose byte order explicitly.

Reproduce from the repository root:

```sh
curl -fsSL https://raw.githubusercontent.com/jetify-com/typeid/main/spec/valid.json -o /tmp/relisten-v4-typeid-vectors.json
dotnet run --file docs/design/2026-10-29-v4-api/experiments/legacy-id-codec.cs -- /tmp/relisten-v4-typeid-vectors.json
```

The fetched vector file had SHA-256
`af5a9cf2447d757b9354f33861d5d83f4e3244487eac191e37021013ce0c17e3`.
The upstream `main` URL may change; check the hash when reproducing these results.

Not tested: library acceptance of legacy bits, prefixes, malformed ID handling
as an API contract, Newtonsoft conversion, route binding, database reads, or
mobile migration. These remain implementation checks. The experiment is
supporting evidence, not a new production codec or package recommendation.
