# Relisten documentation

For the catalog and account APIs, start with the [API overview](api/README.md).
It links the final contracts, mobile sync rules, derived counts and deployment
procedure. Swagger is the field-level reference; the docs explain ownership and
behavior that a schema alone cannot express.

- [Local services](../local-dev/README.md): databases, API startup, dummy/Google sign-in and tests.
- [Ingestion](ingestion/README.md): import architecture, provider rules and operations.
- [Normalization](../Relisten.Normalization/README.md): shared vocabulary and storage contracts.
- [Stored popularity](popularity/README.md): score publication, demand refresh and failure recovery.
- [Archive artist indexing](archive-org-artist-indexing.md): Archive collection discovery.

The dated account/Sonos and playlist documents under `architecture/`, `design/`
and `plans/active/` contain broader work beyond the current account release.
Use the [current account contract](api/accounts.md) for implemented identity and
favorites behavior. Those older designs do not override it.
