using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>
/// Canonical alias review applies across the current person component without moving or
/// rewriting original assertions, review history or support. Reversal restores independence.
/// </summary>
[Migration(58, "Apply original alias review verdicts to current component visibility")]
public sealed class AddCanonicalAliasReviewVisibility : Migration
{
    public const string SchemaSql = """
        -- Work locks these candidate scopes before owner and domain rows. Take the same
        -- locks before relation DDL so a metadata writer cannot hold a candidate scope while
        -- waiting for our history index lock. Old matching snapshots become stale at commit.
        SELECT generation FROM normalization.candidate_universe_revisions
            WHERE domain IN ('compositions','people')
                AND scope_uuid='00000000-0000-0000-0000-000000000000'::uuid
            ORDER BY domain COLLATE "C",scope_uuid FOR UPDATE;

        CREATE INDEX person_alias_history_owner_name
            ON normalization.person_aliases_history(person_uuid,name_key);
        -- Review writes start from an original owner. Current alias reads instead filter
        -- the UNION below by name before joining the component, so keep both access paths.
        CREATE INDEX person_alias_history_name_owner
            ON normalization.person_aliases_history(name_key,person_uuid);

        -- Decide each original person's current claim before combining merged identities.
        -- Accepting an existing alias supersedes same-owner siblings and records their old
        -- automatic rows in history. A current reviewed acceptance therefore overrides its
        -- own negatives/history. Acceptance on another original is not that restoration.
        CREATE VIEW normalization.person_alias_review_exclusions AS
            SELECT withdrawn.person_uuid,withdrawn.name_key
            FROM (
                SELECT person_uuid,name_key FROM normalization.person_aliases
                    WHERE basis='reviewed' AND state IN ('rejected','superseded')
                UNION
                SELECT person_uuid,name_key FROM normalization.person_aliases_history
            ) AS withdrawn
            WHERE NOT EXISTS (
                SELECT 1 FROM normalization.person_aliases restored
                WHERE restored.person_uuid=withdrawn.person_uuid
                    AND restored.name_key=withdrawn.name_key
                    AND restored.basis='reviewed' AND restored.state='accepted'
            );

        CREATE OR REPLACE VIEW normalization.current_person_aliases AS
            SELECT alias.* FROM normalization.person_aliases alias
            WHERE alias.state='accepted' AND (
                alias.basis='reviewed'
                OR EXISTS(SELECT 1 FROM normalization.person_alias_unmanaged_support support
                    WHERE support.alias_uuid=alias.uuid)
                OR EXISTS(SELECT 1 FROM normalization.person_alias_contributor_support support
                    JOIN normalization.source_actor_alias_assignments assignment ON assignment.uuid=support.assignment_uuid
                    WHERE support.alias_uuid=alias.uuid AND support.retired_work_uuid IS NULL
                        AND assignment.retired_work_uuid IS NULL)
            ) AND NOT EXISTS (
                -- Seed only this alias's current component. A global identity-resolution
                -- scan is unnecessary for the name/person predicates of existing readers.
                -- UNION terminates repeated paths; only current redirect edges participate.
                WITH RECURSIVE component(uuid) AS (
                    SELECT alias.person_uuid
                    UNION
                    SELECT CASE WHEN redirect.retired_uuid=member.uuid
                        THEN redirect.survivor_uuid ELSE redirect.retired_uuid END
                    FROM component member JOIN normalization.person_redirects redirect
                        ON redirect.retired_uuid=member.uuid OR redirect.survivor_uuid=member.uuid
                )
                SELECT 1 FROM component member
                JOIN normalization.person_alias_review_exclusions exclusion
                    ON exclusion.person_uuid=member.uuid AND exclusion.name_key=alias.name_key
            );

        -- A changed view does not execute entity triggers. Invalidate facts prepared with
        -- the former alias interpretation without rewriting an identity or accepted row.
        SELECT normalization.bump_candidate_universes(ARRAY['compositions','people'],
            ARRAY['00000000-0000-0000-0000-000000000000'::uuid,
                '00000000-0000-0000-0000-000000000000'::uuid]);
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Retain canonical review exclusions on rollback; stop incompatible writers instead of exposing withdrawn aliases."
        );
}
