using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>Supports original-owner assertions and reverse identity context in candidate snapshots.</summary>
[Migration(32, "Index normalization candidate fact projections")]
public sealed class AddNormalizationCandidateFactIndexes : Migration
{
    public const string SchemaSql = """
        -- Candidate facts start at selected survivors and retain assertions on retired owners.
        CREATE INDEX person_redirect_survivors ON normalization.person_redirects(survivor_uuid,retired_uuid);
        CREATE INDEX venue_redirect_survivors ON normalization.venue_redirects(survivor_uuid,retired_uuid);

        -- A credit keeps citations from previous revisions for audit. Matching reads only
        -- the citations attached to the current assertion, without scanning that history.
        CREATE INDEX recording_credit_current_evidence ON normalization.recording_credit_evidence(credit_uuid,credit_revision);

        CREATE INDEX work_credit_composition_facts ON normalization.work_credits(composition_uuid,uuid) WHERE NOT retracted;
        CREATE INDEX repertoire_external_facts ON normalization.music_external_identities(repertoire_song_uuid,uuid)
            WHERE NOT retracted AND repertoire_song_uuid IS NOT NULL;
        CREATE INDEX composition_external_facts ON normalization.music_external_identities(composition_uuid,uuid)
            WHERE NOT retracted AND composition_uuid IS NOT NULL;
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Disable normalization workers and readers on rollback; retain candidate fact indexes."
        );
}
