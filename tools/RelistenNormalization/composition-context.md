# Artist display context for composition inference

Composition inference needs the artist name attached to each scoped song. The
completed music catalogue already retains those names. This export supplies the
literal context without repeating the song export or reading live providers.

Run `composition_context.py` with the completed music input and output manifests
and their SHA256 pins:

```sh
python3 tools/RelistenNormalization/composition_context.py \
  --music-input-manifest <music-input>/manifest.json \
  --music-input-sha256 <input-manifest-sha256> \
  --music-output-manifest <music-output>/manifest.json \
  --music-output-sha256 <output-manifest-sha256> \
  --output <new-artist-context-directory>
```

The exporter reads the exact catalogue image named by the input manifest. It writes
one `artists.jsonl.gz` row per `(scope, artistUuid)`, including null names. Each row
contains `name`, `musicBrainzId` and an `origin` that identifies the catalogue SHA,
`artists` table, scoped key and `name` field. The catalogue does not retain artist
MusicBrainz IDs, so `musicBrainzId` stays null. The name is display context; it does
not establish authorship or a cross-scope artist identity.

The current full export has 4,929 rows: 4,363 catalogue, 264 local and 302 native.
It preserves the catalogue's two zero-UUID `Unknown artist` entries as unknown
context. The artist stream is 196,073 bytes; the complete bundle is 201,232 bytes.

The completion manifest pins both music manifests, the catalogue image and
generation, the artist stream and the original configured-source metadata. A
catalogue name can replace an earlier source spelling during catalogue creation.
The cited frozen cell records that result; the configured sources do not claim
which reader supplied an overwritten name.

The composition batch can accept this manifest as its optional
`artistContextManifest` artifact `{path, bytes, sha256, rows: 0}`. It joins rows by
the exact scope and artist UUID. Missing context remains unknown. Source context
must remain visible to inference and its cache identity; a performer name does not
turn into a composer credit.

The default export cap is 16 MiB, with a 5.5 GiB free-space reserve. A changed
catalogue or music manifest, insufficient space or an exceeded cap prevents the
completion manifest. An existing destination is never overwritten. These files
do not change the live music browser or its accepted work links.
