# Persisted operations and edge caching

The server accepts deployment-owned queries at
`GET /api/v4/graphql/persisted/{name}?variables={encoded-json}` through Hot
Chocolate's persisted-operation pipeline. The ordinary POST endpoint still accepts
arbitrary queries with `Cache-Control: no-store`.

The production registry starts empty. Files in [operations/](operations/) are
examples and integration fixtures; they are not published operations. There is
no runtime registration, APQ handshake, external query store, or response cache
inside the API.

## Add an operation

1. Add one UTF-8 `.graphql` file under `RelistenApi/GraphQL/Operations/` in the
   appropriate folder. Use a descriptive, versioned filename such as
   `artist-page-v1.graphql`. Its basename (`artist-page-v1`) is the public ID and
   must be unique across folders. Each file must contain exactly one named query;
   fragments can be in the same file. The project embeds files into the API assembly.
2. Choose the folder for the most frequently changing selected data:
   - `Catalog/`: five-minute edge TTL.
   - `Rankings/`: 30-second edge TTL, including catalog queries that select scores.
   - `Live/`: no caching, including any query that selects live activity.
3. Exercise it with representative variables through POST and add or extend a
   focused HTTP contract test where the client needs coverage. Syntax and the
   single-query requirement are checked at startup; Hot Chocolate validates
   fields, variables, costs and depth during execution.
4. Give the client the filename without `.graphql`. Deploy the server document
   before shipping the client that refers to it.

For example, put this query in `Catalog/artist-page-v1.graphql`:

```graphql
query ArtistPage($artist: ID!) {
  artist(id: $artist) { id name showCount }
}
```

Then execute it:

```sh
curl --get --fail-with-body \
  -H 'Accept: application/json' \
  --data-urlencode 'variables={"artist":"artist_3qmp7zjbg1rpebhtyfy43096vj"}' \
  'http://127.0.0.1:3823/api/v4/graphql/persisted/artist-page-v1'
```

The variable names must match the query. Use a consistent JSON serialization in
clients to improve cache reuse. Every variable, including filters, page size and
cursor, stays in the URL/cache key. Large variable sets can use POST instead.
Hot Chocolate also supports an optional GraphQL operation name after the public
ID and POST requests on the persisted route; POST responses remain uncached.

Published IDs are immutable: change a query by adding `artist-page-v2.graphql`,
then update the client to use `artist-page-v2`. Keep v1 until deployed clients no
longer use it. Do not rewrite a published document under the same ID. This is a
small source-controlled registry; versioning is explicit rather than a client
build pipeline.

## Response policy

Only successful HTTP 200, single-result, anonymous GET requests with exactly
`Accept: application/json` and no query parameters other than `variables` are
eligible. Catalog responses use `public, max-age=0, s-maxage=300`; rankings use
`s-maxage=30`. The browser must revalidate while the edge may reuse its response.

All errors, including partial GraphQL errors on HTTP 200, remain `no-store`.
Unknown IDs, invalid variables, live operations, `GraphQL-Cost` diagnostics,
authorization/cookie requests, and other response representations also remain
uncached. Cost, row, read-concurrency and deadline limits are unchanged. Cache
hits serve a previous result; they do not open new database snapshots or queue
popularity refreshes. Misses use the same shared REST readers and projections.

Responses also vary by Accept, diagnostics, authorization and cookies for other
HTTP caches. The JSON-only edge policy avoids a custom Cloudflare cache key for
content negotiation. The Cloudflare rule below must use the same request restrictions so a diagnostic
or differently negotiated request cannot receive an existing cached result.

## Cloudflare rollout

The API emits cache policy; deploying it does not create a Cloudflare rule.
Cloudflare needs an explicit JSON cache eligibility rule for this route. Restrict
it to the catalog hostname and use this expression:

```text
(http.request.method eq "GET"
 and starts_with(http.request.uri.path, "/api/v4/graphql/persisted/")
 and len(http.request.headers["accept"]) eq 1
 and http.request.headers["accept"][0] eq "application/json"
 and not has_key(http.request.headers, "graphql-cost")
 and not has_key(http.request.headers, "authorization")
 and not has_key(http.request.headers, "cookie"))
```

Set eligibility to **Eligible for cache**, use origin cache headers with caching
disabled when they are absent (`edge_ttl.mode: bypass_by_default`), and respect
origin browser TTL. Preserve the full query string in the cache key. Leave other
requests bypassed and ensure broader rules do not override `no-store` or these
exclusions. No Worker or custom header cache key is required.
[Cloudflare rule settings](https://developers.cloudflare.com/cache/how-to/cache-rules/settings/)
describe these controls.

After deployment and the first real registration, request the same URL twice
with the JSON Accept header: check `Cache-Control`, `CF-Cache-Status` and `Age`.
Check that changing variables returns the corresponding result and cost requests
bypass the cache. These are rollout checks; local HTTP tests verify origin policy
and execution, not Cloudflare cache hits. Ordinary changes expire by TTL; purge
cached operation URLs if removing an operation or shortening its cache policy
must take effect immediately. Cache-key-dependent purge restrictions are
[documented by Cloudflare](https://developers.cloudflare.com/cache/how-to/cache-rules/settings/).
