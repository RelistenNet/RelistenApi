# Archive.org collection support

The current [Relisten collections design](2026-10-17-relisten-collections.md) replaces this earlier proposal. Read it for the distinction between upstream discovery collections, canonical performers, and Relisten browsing collections. Use the separate [execution plan](../plans/active/2026-10-17-archive-collections/README.md) for work order.

The retained decisions are to reuse canonical sources across collections and keep membership independent of artist identity. Artist approval is reviewed and may be done in bulk. Neither a collection-wide trust flag, a minimum recording count, nor fuzzy name matching approves an artist. Aadam needs existing-source reuse and supported publication for existing artists; the present Taper’s Section release serves reviewed new artists only.

Implement current intake against its [contracts](../plans/active/2026-10-16-taperssection-intake/contracts.md). Specify the collection migration and interfaces under the new design before implementing those extensions.
