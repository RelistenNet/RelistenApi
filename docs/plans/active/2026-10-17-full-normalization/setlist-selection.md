# Choose a show's display setlist

A show can have several accepted setlist accounts. Select one account for display without declaring its source metadata reviewed. The other accounts, passages and evidence remain available for search and correction.

`NormalizedMusicService.ApplySelectionAsync` makes this choice. It changes the existing `setlist_editions.selected` flags and records a separate editorial decision. It does not create a music revision, change an edition's evidence or basis, or invalidate its source owner.

## Prepare and apply a choice

The caller is an operator or a future review interface. First read `ReadSelectionContextAsync(showUuid, proposedEditionUuid)`. The result contains the current choice, the proposed edition's metadata revision, the show's artist and the selection owner's original observation. The read uses one primary, read-only repeatable-read transaction. It does not create an owner.

Before the first decision, register `(songs, show_setlist_selection, showUuid)` through `NormalizationWorkService.BeginWorkAsync`. This owner has no extraction inputs or dependencies. Read the context again and construct `SetlistSelectionDecision` with:

- A new operation UUID and the selection owner's subject UUID.
- The show UUID and the context's editorial revision.
- `ExpectedSelectedEditionUuid`, including null when nothing is selected.
- The target edition UUID and its positive metadata revision. To clear the choice, both target fields are null.
- The operator's reason. Pass the trusted operator name separately to `ApplySelectionAsync`.

The ledger uses the existing `accept`, `replace` and `retract` actions for an initial choice, a switch or renewed choice, and a clear. The typed payload identifies display selection and includes the previous choice, so the first switch or clear of a legacy flag has a complete audit record. Existing flags do not acquire an invented human review or new metadata history.

The target must be active and assigned to the exact available show and its artist. A stale target revision or changed editorial revision raises `NormalizationConflictException`; reread the context before submitting a new command. A missing or ineligible target raises an argument error. Reusing an operation UUID with a different command conflicts. Exact replay returns the original receipt before checking today's edition or catalog state.

## Keep source updates independent

Edition metadata belongs to `(songs, setlist, editionUuid)`. Display choice belongs to the show's selection owner. Selecting an edition does not mark the edition's contents reviewed, so provider corrections can continue under their existing authority.

`PreserveEditionSelectionAsync` locks the current edition row before `SaveEdition` validates and persists metadata. A source's default `Selected=false` preserves the physical choice. Incoming `true` can retain a selected row but cannot select an unselected row, even through a source metadata review.

A selected edition cannot be retracted or assigned to another show. Clear the display choice first, then apply the source correction. This explicit sequence avoids having source metadata silently choose another display account. A deleted catalog show can still have a retained selected account; the operator can clear it, but cannot make a new choice until the show exists again.

Selection locks the current and target edition rows in UUID order after locking its own editorial owner. Source writers lock their edition row and never lock the selection owner. No additional source owners are marked reviewed or invalidated. A switch clears the old flag before setting the target, inside one transaction; cancellation or failure rolls back the flags and review receipt together.

## Read current selection and preserve history

Public normalized setlist reads expose the physical `selected` flag. Typed `MusicRecord<SetlistEditionChange>` and full-structure reads overlay that flag on the immutable source payload. An old metadata payload can therefore contain a different historical boolean without making today's display result inconsistent.

Selection is excluded from source-claim semantic comparison and rejection matching. Changing a display choice must not make rejected source metadata look like a new claim. Historic music revisions, evidence and completed Work receipts remain unchanged.

This service adds no HTTP write endpoint, migration or table. Existing selected flags and the unique selected-show index remain in place. Operators must use the selection command for new choices; source writers cannot bypass it. Rolling back the service leaves the physical flags and editorial audit intact.

## Verification

`TestNormalizationSetlistSelection` exercises selection, switching and clearing through the public service; source-authority separation; typed and public reads; stale contexts; legacy flags; deleted shows; concurrent commands; metadata row-lock races; cancellation; and a failed switch that rolls back before an exact retry. The surrounding music and full-structure tests verify that the shared metadata path uses the same preservation rule.
