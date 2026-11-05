# Persisting catalog responses

Use this when implementing a repository that saves catalog responses locally.
The API supplies resources and complete collections; it does not synchronize the
client database or declare which unrelated records to delete. Keep the HTTP
transport separate from repository mapping and persistence.

## Replace only the collection that was requested

Identify a cached query by its endpoint and normalized filters. In one local
transaction, upsert returned entity fields, replace that query's ordered membership
when complete, and save the response's ETag. A complete empty list clears its own
membership. Fetch or apply failure leaves the previous data and validator intact.

- An artist collection owns only that collection: years, venues, songs or tours.
- Year, venue, song and tour details own their returned `shows` membership.
- Full show/source detail owns the children fully returned for that owner. A show summary or shallow parent is only a field patch.
- A song's `/tracks` owns its file-match membership, independently of the song detail's shows. Reviews and eras similarly own their separate collections.
- Search, date, recent and limited queries own only their exact filtered result.
- Resolver entities are patches; rankings and live summary are transient online views.

An absent shared entity does not authorize deletion. Other queries, downloads,
queue/history entries or favorites may still reference it. A compact patch must
not clear richer metadata or child lists it did not supply. Track normalization
is one ordered interpretation to replace, not a collection of new file identities.

For example, an empty venue detail removes all shows from that venue's cached list.
It does not delete those show records or their downloaded files. Loading a show
summary afterward cannot clear the full recording hierarchy saved earlier.

## An ETag validates a response, not each stored object

The server hashes represented JSON, including normalization and scores. Changes
can alter an ETag without advancing imported `updated_at`. Conditional GET saves
transfer and client application work, but still performs the origin read and
serialization. It is not a revision cursor or a write-concurrency token.

Ordinary catalog responses allow 30 minutes of shared/client caching, followed by
a 60-second stale-while-revalidate window. Conditional requests still receive an
ETag on both 200 and 304 responses. Live summary has its own shorter lifetime;
live history, random results, commands and errors use `no-store`.

Different endpoints can patch the same entity. Shared fields apply in response
arrival order; the client suppresses superseded requests for the same query.
There is no total order across different queries. An older response from another
endpoint can overwrite a field while the owning query's ETag remains unchanged.
Its next conditional GET can return 304 and leave that value in place. Manual
refresh fetches a body to restore the owning response's fields. Keep this limitation
in mind before treating a saved ETag as proof that every shared object is current.

Use a saved validator only when its query membership and required root records
are still available locally; otherwise fetch a body. A stored empty membership is
a valid result. This is a local usability check, not an audit of every related
record. The mobile executor owns freshness and retries; repositories apply
responses and validators atomically. Account membership uses its own revision/cursor protocol.
