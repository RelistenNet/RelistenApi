# Browse complete directly supported people groups

The shared-people directory adds provisional person profiles to the existing people reader. A profile includes every exactly mapped recording occurrence in its anchor bucket and directly supported buckets. The C# export owns matching; Python uses its decisions and the occurrence mapper's exact retained row IDs. Equal names, shared accounts and graph paths cannot add members.

The overlay is a separate SQLite file. It does not copy or change the base directory. Credit roles, recording homes, captured show IDs and original evidence remain owned by the base generation. Role-free alias observations appear separately on a recording and supply no role or person kind. Unmapped or ambiguous occurrences remain in the original mapper ledger and are counted on affected profiles.

Build from completed, pinned exports into a new path:

```sh
python3 tools/RelistenNormalization/shared_people_directory.py \
  --database /absolute/path/to/directory.sqlite3 \
  --shared-run /absolute/path/to/shared-support-run \
  --mapping-run /absolute/path/to/occurrence-map-run \
  --output /absolute/path/to/new-shared-people.sqlite3
```

The builder checks the mapping's base and shared-export hashes, all three export files, mapping output and exact retained source/artist/person/origin ownership. It consumes the complete occurrence ledger, then stores only buckets used in comparisons and their mapped members. The final file is published after a complete build and input recheck. The defaults limit the output file to 512 MiB and preserve 5.5 GiB of free disk. Failure leaves an unpublished `.building` file; use a new output path for a retry. It makes no provider, model or catalog database requests.

Restart the latest people-reader process with the same base directory, URLs and port plus the overlay argument. Preserve older generations at their existing addresses:

```sh
python3 tools/RelistenNormalization/people_directory.py serve \
  --database /absolute/path/to/directory.sqlite3 \
  --shared-people-database /absolute/path/to/new-shared-people.sqlite3 \
  --files-url http://127.0.0.1:18767 \
  --core-url http://127.0.0.1:18765 \
  --port 18777
```

Search and person pages retain their existing interfaces. New IDs use `shared-person:<anchor-bucket-id>` and remain addressable even when identical membership appears under another search result. Existing context-only `person-hypothesis:` URLs remain valid. Profiles show exact recordings, artist and role filters, original credit or alias evidence, and source-owned playable-file links. `/api/shared-person-support` returns the original directed comparison, bucket evidence and representative source coordinates.

A future star containing known conflicting member buckets requires review and is withheld as a broad profile. The builder does not choose between conflicting members. The completed overlay records `reviewRequiredProfiles`; the current source and raw name observations remain accessible.

The server checks the base hash once and pins both files to their read generation. If the base or overlay changes, broad membership becomes unavailable for new requests. Raw names and context-only profiles continue to work. `/api/stats.sharedPeopleOverlay` reports readiness or the stale-generation reason. This overlay does not activate accepted identities or change catalog assignments.

## Current listener pages

The existing [people URL](http://alecs-macbook-pro.tail09dbf.ts.net:18777/) now serves the full source pass. [Tonedeaf](http://alecs-macbook-pro.tail09dbf.ts.net:18777/?person=shared-person%3Apeople_facts%3A9de2bd6e8a4f53298d78c7f3c56299b9) has 2,370 recordings across 403 artists; [Timothy Brown](http://alecs-macbook-pro.tail09dbf.ts.net:18777/?person=shared-person%3Apeople_facts%3Ad40f1b4fab9c7492c3cd9146fbd5fcc5) has 424 across 105. Artist and role filters lead to the original recording's files and retained audio URL.

The 58 MiB overlay adds 809 distinct search profiles to the 1,472 earlier context profiles. Its directly supported pages cover 18,120 recordings across 1,536 artists. Forty-eight ambiguous credit occurrences remain unassigned and accessible through the original data. The 408 role-free source observations were mapped exactly, but none has direct support for these larger profiles; they remain separate alias observations.

The retained build is `local-dev/normalization-data/2026-10-18-people-shared-directory-v1/`. Its inputs and output are pinned in the build metadata. Refresh into a new overlay and restart the same URL after the new generation is complete; retain the prior overlay for rollback.
