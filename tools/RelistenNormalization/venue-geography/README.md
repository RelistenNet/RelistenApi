# Offline venue geography

The venue shadow adapter can resolve city wording against a pinned GeoNames
reference. A shared `geonames:<id>` identifies a populated place or district. It
does not identify a concert venue. Physical venue proposals still need compatible
provider venue IDs or exact street-address evidence, with room/site scope kept
separate. Gazetteer coordinates describe the locality; legacy venue coordinates
remain unclassified.

Acquire a new immutable reference directory:

```sh
python3 tools/RelistenNormalization/venue-geography/acquire.py \
  local-dev/normalization-data/geonames-cities500-new
```

The command prints `<manifest-path>#sha256=<digest>`. Set that complete value as
`RELISTEN_SHADOW_VENUE_GEOGRAPHY` when running `shadow`. Each adapter method records
the manifest digest; changing the reference requires a new run. No geographic
network requests occur during matching. Acquisition downloads three general
public files and sends no catalog data.

`acquire.py` retains the original `cities500.zip`, `admin1CodesASCII.txt` and
`readme.txt`, their URLs, hashes, sizes and server modification dates. The adapter
authenticates these bytes before loading. [GeoNames documents the format and
license](https://download.geonames.org/export/dump/readme.txt): cities500 includes
populated places over 500 people and administrative seats, with alternate names.
It is an incomplete geographic reference. The [daily download](https://www.geonames.org/about.html)
is available under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).
No web-service account or per-query API quota is involved. Each output retains
GeoNames attribution and the reference digest; retained downloads are unchanged.

Matching unions exact normalized aliases, then applies every supplied country
and administrative-region constraint. One distinct geographic ID yields a
proposal. Ambiguous names retain candidates; missing countries, unrecognized
constraints and absent towns remain unresolved. No population ranking or
coordinate proximity breaks ties. District feature codes remain visible.
Historical aliases can help propose a geographic feature, but their dates and
historical administrative boundaries remain unknown.

The initial full shadow pass processed 216,332 venues and 292,523 recording
usages. It emitted 186,288 proposed venue interpretations and 30,044 unresolved
ones. Of all venue rows, 199,315 retained separate physical keys; 16,799 used a
qualified provider reference and 218 used exact address evidence. Parsed locality
wording alone left 169,271 proposed rows without shared physical identity
evidence. The largest unresolved reason was locality interpretation (29,204),
followed by missing names (489) and multiple places in one legacy row (351).
These counts describe the earlier adapter, before geographic resolution; they
are coverage diagnostics, not measured matching accuracy.

All 216,332 rows lack structured city/address fields and `past_names`. Of their
location strings, 187,789 have one comma, 22,240 none, 6,057 two and 246 more.
`Unknown Location` accounts for 10,500 rows. There are 21,684 coordinate pairs,
including repeated city centroids. These observations explain the locality
reference and the limits on physical matching. The retained population is
`2026-10-18-shadow-full-raw-v1/core-inputs-manifest-v1.json`; baseline outcomes are
`2026-10-18-shadow-complete-core-ranges-v1/venues/` under
`local-dev/normalization-data/`.

[The population receipt](population-review.json) records the immutable input pins
and an independent metadata-only Python probe. That probe proposes one geographic
ID for 164,164 venues (75.89%), covering 228,138 direct recording venue references
(77.99%). Its parser differs from the C# adapter; these are reference-coverage
measurements, not new adapter runtime results or accuracy estimates. Cities500
omits some small towns, and aliases can name multiple features in one region.

The probe also found 71,819 recordings whose source and show refer to different
venue rows. Different row IDs do not prove different places. For 1,913 of those
recordings, the two rows propose different localities; 19,628 have an unresolved
locality on at least one side. All setlist.fm and PhantasyTour venue rows currently
lack direct source venue references. Cross-provider venue correspondence still
needs evidence; geographic proposals do not move recordings or replace source
venue ownership.
