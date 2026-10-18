using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationReadService
{
    /// <summary>Maximum UTF-8 bytes of one projected account/set/passage annotation group.</summary>
    public const int MaximumAnnotationGroupBytes = 16 * 1024;
    public const int MaximumAnnotationTargets = 128;

    // Even one-digit definition indexes need a digit and an array separator. Stop excessive
    // reference fan-out before constructing public references; the final wire-size check also
    // accounts for larger indexes, evidence, JSON escaping and the group's metadata.
    private const int MaximumAnnotationReferenceLinks = MaximumAnnotationGroupBytes / 2;

    /// <summary>
    /// Reads source-qualified annotations from the accepted image, without re-decoding current
    /// provider data. Oversized groups remain visible with size_limit and no partial detail.
    /// Cursors pin the image revision; a correction requires restarting pagination.
    /// </summary>
    public Task<NormalizationPage<NormalizedSetlistAnnotationGroup>?> ListSetlistAnnotationsAsync(
        Guid editionUuid,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default
    )
    {
        const string route = "setlist_annotations";
        const string order = "source_scope_entry";
        var after = ParseCursor(cursor, route, editionUuid, null, order, limit);
        long index = -1;
        if (
            after != null
            && (
                !long.TryParse(
                    after.Sort,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out index
                )
                || index < 0
            )
        )
        {
            throw new ArgumentException("Invalid annotation cursor.", nameof(cursor));
        }
        return ReadAsync(
            async (c, t) =>
            {
                if (
                    !await c.ExecuteScalarAsync<bool>(
                        new CommandDefinition(
                            "SELECT EXISTS(SELECT 1 FROM normalization.setlist_editions WHERE uuid=@editionUuid AND NOT retracted)",
                            new { editionUuid },
                            t,
                            cancellationToken: cancellationToken
                        )
                    )
                )
                {
                    return null;
                }
                var image = await c.QuerySingleOrDefaultAsync<AnnotationImage>(
                    new CommandDefinition(
                        """
                        SELECT r.uuid,r.revision,r.basis,r.decision_uuid
                        FROM normalization.setlist_structures s JOIN normalization.music_revisions r ON r.uuid=s.revision_uuid
                        WHERE s.edition_uuid=@editionUuid AND NOT s.retracted
                        """,
                        new { editionUuid },
                        t,
                        cancellationToken: cancellationToken
                    )
                );
                if (after != null && image?.uuid != after.Uuid)
                {
                    throw new ArgumentException(
                        "The accepted structure changed. Restart annotation pagination.",
                        nameof(cursor)
                    );
                }
                if (image == null)
                {
                    return new NormalizationPage<NormalizedSetlistAnnotationGroup>();
                }
                var rows = (
                    await c.QueryAsync<AnnotationRow>(
                        new CommandDefinition(
                            AnnotationPageSql,
                            new
                            {
                                revision = image.uuid,
                                after = index,
                                take = limit + 1,
                                maximumBytes = MaximumAnnotationGroupBytes,
                            },
                            t,
                            cancellationToken: cancellationToken
                        )
                    )
                ).ToArray();
                var targets = await ReadAnnotationTargetsAsync(
                    c,
                    t,
                    image.uuid,
                    rows,
                    cancellationToken
                );
                var statuses = await ReadAnnotationStatusesAsync(c, t, rows, cancellationToken);
                var items = rows.Take(limit)
                    .Select(row =>
                        ProjectAnnotationGroup(editionUuid, image, row, targets, statuses)
                    )
                    .ToArray();
                return new NormalizationPage<NormalizedSetlistAnnotationGroup>
                {
                    items = items,
                    next_cursor =
                        rows.Length > limit
                            ? EncodeCursor(
                                route,
                                editionUuid,
                                null,
                                order,
                                items[^1].index.ToString(CultureInfo.InvariantCulture),
                                image.uuid
                            )
                            : null,
                };
            },
            cancellationToken
        );
    }

    // Only selected annotation fields cross the database boundary. The full retained Account,
    // venue, raw document and unrelated structure citations never enter a public response.
    private const string AnnotationPageSql = """
        WITH image AS MATERIALIZED (SELECT payload FROM normalization.music_revisions WHERE uuid=@revision),
        sources AS MATERIALIZED (
            SELECT (s.ordinal-1)::integer AS source_index,s.value AS source,
                s.value->'Account'->'Account' AS account,s.value->'Account'->'Passages' AS passages
            FROM image CROSS JOIN LATERAL jsonb_array_elements(payload->'Sources') WITH ORDINALITY s(value,ordinal)
        ), coordinates AS (
            SELECT source_index,'account'::text AS scope,0 AS scope_order,0::bigint AS entry,
                NULL::integer AS set_index,NULL::uuid AS passage_uuid FROM sources
            UNION ALL
            SELECT source_index,'set',1,s.ordinal,s.ordinal::integer-1,NULL::uuid
            FROM sources CROSS JOIN LATERAL jsonb_array_elements(account->'Sets') WITH ORDINALITY s(value,ordinal)
            UNION ALL
            SELECT source_index,'passage',2,p.ordinal,NULL::integer,(p.value->>'ProposalUuid')::uuid
            FROM sources CROSS JOIN LATERAL jsonb_array_elements(passages) WITH ORDINALITY p(value,ordinal)
        ), ordered AS (
            SELECT *,row_number() OVER(ORDER BY source_index,scope_order,entry)-1 AS index FROM coordinates
        ), page AS MATERIALIZED (
            SELECT * FROM ordered WHERE index>@after ORDER BY index LIMIT @take
        ), selected AS (
            SELECT page.*,source,account,passages->(entry::integer-1)->'Passage' AS passage,
                account->'Sets'->set_index AS observed_set
            FROM page JOIN sources USING(source_index)
        ), projected AS (
            SELECT selected.*,
                EXISTS(SELECT 1 FROM image CROSS JOIN LATERAL jsonb_array_elements(payload->'UnresolvedPassages') u
                    WHERE (u->>'SourceIndex')::integer=source_index AND (u->>'ProposalUuid')::uuid=passage_uuid) AS unresolved,
                CASE scope WHEN 'account' THEN
                    jsonb_build_object('Origin',account->'Origin','Text',account->'Text','Footnotes',account->'Footnotes',
                        'Performers',account->'Performers')
                WHEN 'set' THEN jsonb_build_object('Origin',account->'Origin','Annotations',observed_set->'Annotations')
                ELSE jsonb_build_object('Origin',account->'Origin','Title',passage->'Title',
                    'Annotations',passage->'Annotations','PerformerAnnotations',passage->'PerformerAnnotations',
                    'Tape',passage->'Tape','Segue',passage->'Segue','TransitionMarker',passage->'TransitionMarker',
                    'FootnoteNumbers',passage->'FootnoteNumbers','DefinitionsCaptured',account ? 'Footnotes',
                    'DefinitionNumbers',(SELECT COALESCE(jsonb_agg(jsonb_build_object('Index',f.ordinal-1,'Number',f.value->'Number'->'Value')),'[]')
                        FROM jsonb_array_elements(COALESCE(account->'Footnotes','[]')) WITH ORDINALITY f(value,ordinal))) END AS node
            FROM selected
        )
        SELECT index,source_index,(source->>'ProposalAttemptUuid')::uuid AS proposal_attempt_uuid,
            (source->>'InputRevisionUuid')::uuid AS input_revision_uuid,(source->>'AccountIndex')::integer AS account_index,
            scope,set_index AS source_set_index,passage_uuid,unresolved,octet_length(node::text)>@maximumBytes AS oversized,
            CASE WHEN octet_length(node::text)<=@maximumBytes THEN node::text END AS details
        FROM projected ORDER BY index
        """;

    private static async Task<
        IReadOnlyDictionary<(int Source, string Kind, string Key), AnnotationTargets>
    > ReadAnnotationTargetsAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid revision,
        AnnotationRow[] rows,
        CancellationToken ct
    )
    {
        var keys = rows.Where(row => row.scope != "account")
            .Select(row => new
            {
                source_index = row.source_index,
                kind = row.scope,
                source_key = row.source_set_index?.ToString(CultureInfo.InvariantCulture)
                    ?? row.passage_uuid!.Value.ToString("D"),
            })
            .ToArray();
        var result = await c.QueryAsync<AnnotationTargets>(
            new CommandDefinition(
                """
                WITH requested AS (SELECT * FROM jsonb_to_recordset(@keys::jsonb)
                    AS x(source_index integer,kind text,source_key text))
                SELECT requested.source_index,requested.kind,requested.source_key,count(link.target_uuid) AS count,
                    CASE WHEN count(link.target_uuid)<=@maximumTargets
                        THEN COALESCE(array_agg(link.target_uuid ORDER BY link.target_uuid) FILTER(WHERE link.target_uuid IS NOT NULL),ARRAY[]::uuid[])
                        ELSE NULL END AS uuids
                FROM requested LEFT JOIN normalization.setlist_structure_correspondences link
                    ON link.revision_uuid=@revision AND link.source_index=requested.source_index
                        AND link.kind=requested.kind AND link.source_key=requested.source_key
                GROUP BY requested.source_index,requested.kind,requested.source_key
                """,
                new
                {
                    revision,
                    keys = JsonSerializer.Serialize(keys),
                    maximumTargets = MaximumAnnotationTargets,
                },
                t,
                cancellationToken: ct
            )
        );
        return result.ToDictionary(row => (row.source_index, row.kind, row.source_key));
    }

    private static async Task<IReadOnlyDictionary<Guid, string>> ReadAnnotationStatusesAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        AnnotationRow[] rows,
        CancellationToken ct
    )
    {
        var ids = rows.Select(row => row.input_revision_uuid).ToHashSet();
        foreach (var row in rows.Where(row => row.details != null))
        {
            using var document = JsonDocument.Parse(row.details!);
            CollectAnnotationInputUuids(document.RootElement, ids);
        }
        var statuses = await c.QueryAsync<AnnotationStatus>(
            new CommandDefinition(
                """
                SELECT input.uuid,CASE WHEN resource.current_revision_uuid IS NULL THEN 'missing'
                    WHEN resource.current_revision_uuid=input.uuid THEN 'current' ELSE 'changed' END AS status
                FROM normalization.input_revisions input LEFT JOIN normalization.input_resources resource ON resource.uuid=input.resource_uuid
                WHERE input.uuid=ANY(@ids)
                """,
                new { ids = ids.ToArray() },
                t,
                cancellationToken: ct
            )
        );
        return statuses.ToDictionary(row => row.uuid, row => row.status);
    }

    private static void CollectAnnotationInputUuids(JsonElement value, HashSet<Guid> ids)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name == "InputRevisionUuid" && property.Value.TryGetGuid(out var uuid))
                {
                    ids.Add(uuid);
                }
                else
                {
                    CollectAnnotationInputUuids(property.Value, ids);
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray())
            {
                CollectAnnotationInputUuids(child, ids);
            }
        }
    }

    private static NormalizedSetlistAnnotationGroup ProjectAnnotationGroup(
        Guid edition,
        AnnotationImage image,
        AnnotationRow row,
        IReadOnlyDictionary<(int Source, string Kind, string Key), AnnotationTargets> targets,
        IReadOnlyDictionary<Guid, string> statuses
    )
    {
        var key =
            row.source_set_index?.ToString(CultureInfo.InvariantCulture)
            ?? row.passage_uuid?.ToString("D");
        var mapping =
            key == null ? null : targets.GetValueOrDefault((row.source_index, row.scope, key));
        var oversized = row.oversized || (mapping?.count ?? 0) > MaximumAnnotationTargets;
        Guid[]? sets =
            oversized ? null
            : row.scope == "set" ? mapping?.uuids ?? []
            : [];
        Guid[]? occurrences =
            oversized ? null
            : row.scope == "passage" ? mapping?.uuids ?? []
            : [];
        AnnotationDetails? detail = oversized ? null : new(row.details!, statuses);
        using (detail)
        {
            IReadOnlyList<NormalizedFootnoteReference>? references = null;
            var referenceLimit =
                row.scope == "passage" && detail != null && !detail.TryReferences(out references);
            var selectedDetail = referenceLimit ? null : detail;
            var group = new NormalizedSetlistAnnotationGroup
            {
                index = row.index,
                edition_uuid = edition,
                structure_revision_uuid = image.uuid,
                revision = image.revision,
                basis = image.basis,
                decision_uuid = image.decision_uuid,
                source_index = row.source_index,
                proposal_attempt_uuid = row.proposal_attempt_uuid,
                input_revision_uuid = row.input_revision_uuid,
                account_index = row.account_index,
                scope = row.scope,
                source_set_index = row.source_set_index,
                source_passage_uuid = row.passage_uuid,
                correspondence =
                    row.scope == "account" ? "account"
                    : (mapping?.count ?? 0) > 0 ? "mapped"
                    : row.unresolved ? "unresolved"
                    : "unmapped",
                set_uuids = sets,
                occurrence_uuids = occurrences,
                target_count = mapping?.count ?? 0,
                detail_status =
                    oversized || referenceLimit ? "size_limit"
                    : row.scope == "set" && selectedDetail!.IsMissing("Annotations")
                        ? "not_captured"
                    : "available",
                origin = selectedDetail?.Origin(),
                passage_title = selectedDetail?.ValueText("Title", "title"),
                notes = selectedDetail?.Notes(row.scope == "account" ? "Text" : "Annotations"),
                footnotes = row.scope == "account" ? selectedDetail?.Definitions() : null,
                footnote_references = references,
                performers =
                    row.scope == "set" ? null : selectedDetail?.Performers(row.scope == "account"),
                prerecorded_playback = selectedDetail?.Boolean("Tape"),
                segue = selectedDetail?.Boolean("Segue"),
                transition_marker = selectedDetail?.ValueText(
                    "TransitionMarker",
                    "transition_marker"
                ),
            };
            if (group.detail_status == "size_limit")
            {
                return OmitAnnotationDetails(group);
            }

            // Use the actual HTTP serializer: escaping and derived reference links can make
            // a small retained projection larger after conversion to the public contract.
            var serialized = Newtonsoft.Json.JsonConvert.SerializeObject(
                group,
                Newtonsoft.Json.Formatting.None,
                RelistenApiJsonOptionsWrapper.ApiV3SerializerSettings
            );
            return Encoding.UTF8.GetByteCount(serialized) > MaximumAnnotationGroupBytes
                ? OmitAnnotationDetails(group)
                : group;
        }
    }

    private static NormalizedSetlistAnnotationGroup OmitAnnotationDetails(
        NormalizedSetlistAnnotationGroup group
    ) =>
        group with
        {
            detail_status = "size_limit",
            set_uuids = null,
            occurrence_uuids = null,
            origin = null,
            passage_title = null,
            notes = null,
            footnotes = null,
            footnote_references = null,
            performers = null,
            prerecorded_playback = null,
            segue = null,
            transition_marker = null,
        };

    /// <summary>Maps a bounded typed projection; raw source markup is never used as display prose.</summary>
    private sealed class AnnotationDetails(string json, IReadOnlyDictionary<Guid, string> statuses)
        : IDisposable
    {
        private readonly JsonDocument document = JsonDocument.Parse(json);
        private JsonElement Root => document.RootElement;

        public void Dispose() => document.Dispose();

        public bool IsMissing(string key) =>
            !Root.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null;

        private IEnumerable<JsonElement> Array(string key) =>
            IsMissing(key) ? [] : Root.GetProperty(key).EnumerateArray();

        private NormalizationPublicEvidence Evidence(NormalizedEvidenceReference source) =>
            new()
            {
                input_revision_uuid = source.InputRevisionUuid,
                json_pointer = source.JsonPointer,
                start_code_point = source.StartCodePoint,
                end_code_point = source.EndCodePoint,
                quote = source.Quote,
                evidence_status = statuses.GetValueOrDefault(source.InputRevisionUuid, "missing"),
            };

        public NormalizedAnnotationOrigin? Origin()
        {
            var origin = Root.GetProperty("Origin").Deserialize<ProviderObservationOrigin>()!;
            return new(origin.Item, origin.Scope.ToString(), Evidence(origin.Evidence));
        }

        public NormalizedAnnotationText? ValueText(string key, string kind)
        {
            if (IsMissing(key))
            {
                return null;
            }
            var value = Root.GetProperty(key).Deserialize<ObservedValue<string>>()!;
            return new(kind, "available", value.Value, Evidence(value.Evidence));
        }

        public NormalizedAnnotationBoolean? Boolean(string key)
        {
            if (IsMissing(key))
            {
                return null;
            }
            var value = Root.GetProperty(key).Deserialize<ObservedValue<bool>>()!;
            return new(value.Value, Evidence(value.Evidence));
        }

        public IReadOnlyList<NormalizedAnnotationText>? Notes(string key) =>
            IsMissing(key) ? null : Array(key).Select(Note).ToArray();

        private NormalizedAnnotationText Note(JsonElement element)
        {
            var note = element.Deserialize<ProviderTextObservation>()!;
            var kind = note.Field switch
            {
                "info" => "info",
                "credits" => "source_credit_note",
                "prenotes" => "pre_show",
                "postnotes" => "post_show",
                "footnote" or "footnotes" => "footnote",
                "setlist_notes" => "setlist_notes",
                "tour" => "tour_note",
                "annotation_marker" => "marker",
                "notes" => "notes",
                _ => "unsupported",
            };
            // Older proposals did not distinguish raw markup from display text. Missing format
            // cannot authorize exposing their original text as an HTML-derived display value.
            var captured = element.TryGetProperty("Format", out _);
            var text = note.Format switch
            {
                ProviderTextFormat.PlainText => note.Text.Value,
                ProviderTextFormat.SourceMarkup => note.PlainText,
                _ => null,
            };
            var state =
                kind == "unsupported" ? "unsupported_kind"
                : !captured || text == null ? "not_decoded"
                : "available";
            return new(
                kind,
                state,
                state == "available" ? text : null,
                Evidence(note.Text.Evidence)
            );
        }

        public IReadOnlyList<NormalizedFootnoteDefinition>? Definitions() =>
            IsMissing("Footnotes")
                ? null
                : Array("Footnotes")
                    .Select(
                        (value, index) =>
                        {
                            var number = value
                                .GetProperty("Number")
                                .Deserialize<ObservedValue<int>>();
                            return new NormalizedFootnoteDefinition(
                                index,
                                number?.Value,
                                number == null ? null : Evidence(number.Evidence),
                                Note(value.GetProperty("Text"))
                            );
                        }
                    )
                    .ToArray();

        public bool TryReferences(out IReadOnlyList<NormalizedFootnoteReference>? references)
        {
            var definitions = Array("DefinitionNumbers")
                .Where(value => value.GetProperty("Number").ValueKind != JsonValueKind.Null)
                .ToLookup(
                    value => value.GetProperty("Number").GetInt32(),
                    value => value.GetProperty("Index").GetInt32()
                )
                .ToDictionary(group => group.Key, group => group.ToArray());
            var observed = Array("FootnoteNumbers")
                .Select(element => element.Deserialize<ObservedValue<int>>()!)
                .ToArray();
            long links = 0;
            foreach (var value in observed)
            {
                links += definitions.GetValueOrDefault(value.Value)?.Length ?? 0;
                if (links > MaximumAnnotationReferenceLinks)
                {
                    references = null;
                    return false;
                }
            }

            var captured = Root.GetProperty("DefinitionsCaptured").GetBoolean();
            references = observed
                .Select(value =>
                {
                    // Repeated references share the same immutable index array; no repeated scan
                    // or duplicate allocation is needed to preserve duplicate definitions.
                    var matches = definitions.GetValueOrDefault(value.Value) ?? [];
                    return new NormalizedFootnoteReference(
                        value.Value,
                        !captured ? "not_captured"
                            : matches.Length == 0 ? "missing"
                            : matches.Length == 1 ? "matched"
                            : "ambiguous",
                        matches,
                        Evidence(value.Evidence)
                    );
                })
                .ToArray();
            return true;
        }

        public IReadOnlyList<NormalizedPerformerClaim> Performers(bool account) =>
            Array(account ? "Performers" : "PerformerAnnotations")
                .Select(element =>
                {
                    var name = element.GetProperty("Name").Deserialize<ObservedValue<string>>();
                    var identities = element
                        .GetProperty("Identities")
                        .Deserialize<ObservedProviderIdentity[]>()!;
                    var relationship = account
                        ? "reported_performer"
                        : element.GetProperty("Kind").GetString() switch
                        {
                            "cover_artist" => "cover_performer",
                            "guest_artist" => "guest_performer",
                            _ => "unsupported",
                        };
                    return new NormalizedPerformerClaim(
                        relationship,
                        name == null
                            ? null
                            : new(
                                "performer_name",
                                "available",
                                name.Value,
                                Evidence(name.Evidence)
                            ),
                        identities
                            .Select(identity => new NormalizedAnnotationIdentity(
                                identity.Identity,
                                Evidence(identity.Evidence)
                            ))
                            .ToArray()
                    );
                })
                .ToArray();
    }

    private sealed class AnnotationImage
    {
        public Guid uuid { get; set; }
        public long revision { get; set; }
        public string basis { get; set; } = "";
        public Guid? decision_uuid { get; set; }
    }

    private sealed class AnnotationRow
    {
        public long index { get; set; }
        public int source_index { get; set; }
        public Guid proposal_attempt_uuid { get; set; }
        public Guid input_revision_uuid { get; set; }
        public int account_index { get; set; }
        public string scope { get; set; } = "";
        public int? source_set_index { get; set; }
        public Guid? passage_uuid { get; set; }
        public bool unresolved { get; set; }
        public bool oversized { get; set; }
        public string? details { get; set; }
    }

    private sealed class AnnotationTargets
    {
        public int source_index { get; set; }
        public string kind { get; set; } = "";
        public string source_key { get; set; } = "";
        public long count { get; set; }
        public Guid[]? uuids { get; set; }
    }

    private sealed class AnnotationStatus
    {
        public Guid uuid { get; set; }
        public string status { get; set; } = "";
    }
}
