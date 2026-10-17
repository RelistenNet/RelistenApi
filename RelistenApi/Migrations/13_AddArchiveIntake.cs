using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>Retains intake evidence and publication ownership independently of the rebuildable catalog.</summary>
[Migration(13, "Add reviewed Taper's Section artist intake")]
public sealed class AddArchiveIntake : Migration
{
    // The disposable catalog fixture applies the same additive DDL as both production hosts.
    public const string SchemaSql = """
        CREATE SCHEMA normalization;

        CREATE TABLE normalization.archive_artist_candidates (
            candidate_id uuid PRIMARY KEY,
            creator_key text NOT NULL UNIQUE,
            proposed_name text NOT NULL,
            proposed_slug text NOT NULL,
            reviewed_creator_keys text[] NOT NULL,
            approved boolean NOT NULL DEFAULT false,
            identity_status text NOT NULL DEFAULT 'pending'
                CHECK (identity_status IN ('pending','existing_match','needs_review','cohort')),
            matched_artist_uuids uuid[] NOT NULL DEFAULT '{}',
            representative_observation_ids uuid[] NOT NULL DEFAULT '{}',
            artist_uuid uuid UNIQUE,
            artist_id integer,
            creation_slug text,
            reason text,
            created_at timestamptz NOT NULL DEFAULT now(),
            updated_at timestamptz NOT NULL DEFAULT now(),
            CHECK ((artist_uuid IS NULL AND artist_id IS NULL AND creation_slug IS NULL)
                OR (artist_uuid IS NOT NULL AND artist_id IS NOT NULL AND creation_slug IS NOT NULL))
        );
        CREATE INDEX archive_candidates_approved ON normalization.archive_artist_candidates(candidate_id)
            WHERE approved;
        CREATE INDEX archive_candidates_reviewed_keys ON normalization.archive_artist_candidates
            USING gin(reviewed_creator_keys);

        CREATE TABLE normalization.archive_item_observations (
            observation_id uuid PRIMARY KEY,
            item_identifier text NOT NULL,
            projection_version integer NOT NULL,
            content_hash text NOT NULL,
            observed_at timestamptz NOT NULL DEFAULT now(),
            provider_url text NOT NULL,
            payload jsonb NOT NULL,
            UNIQUE(item_identifier, projection_version, content_hash)
        );

        CREATE TABLE normalization.archive_item_intake (
            item_identifier text PRIMARY KEY,
            latest_observation_id uuid REFERENCES normalization.archive_item_observations(observation_id),
            last_observed_at timestamptz,
            last_attempt_at timestamptz,
            originating_collection text NOT NULL DEFAULT 'taperssection',
            discovered_at timestamptz NOT NULL DEFAULT now(),
            last_discovered_at timestamptz NOT NULL DEFAULT now(),
            candidate_id uuid REFERENCES normalization.archive_artist_candidates(candidate_id),
            rules_version integer,
            interpretation jsonb,
            status text NOT NULL DEFAULT 'pending_observation'
                CHECK (status IN ('pending_observation','awaiting_artist_approval','deferred_existing_artist',
                    'needs_review','eligible','published','paused','unavailable')),
            reason text,
            last_error text,
            attempts integer NOT NULL DEFAULT 0,
            next_attempt_at timestamptz NOT NULL DEFAULT now(),
            published_observation_id uuid REFERENCES normalization.archive_item_observations(observation_id),
            published_rules_version integer,
            published_artist_uuid uuid,
            published_artist_id integer,
            published_display_date text,
            published_source_uuid uuid UNIQUE,
            published_at timestamptz,
            catalog_revision bigint NOT NULL DEFAULT 0,
            rebuilt_revision bigint NOT NULL DEFAULT 0,
            CHECK (rebuilt_revision >= 0 AND catalog_revision >= rebuilt_revision),
            CHECK (published_source_uuid IS NULL OR
                (published_observation_id IS NOT NULL AND published_artist_uuid IS NOT NULL
                 AND published_artist_id IS NOT NULL AND published_display_date IS NOT NULL))
        );
        CREATE INDEX archive_intake_candidate ON normalization.archive_item_intake(candidate_id, item_identifier);
        CREATE INDEX archive_intake_pending ON normalization.archive_item_intake(next_attempt_at, item_identifier)
            WHERE status = 'pending_observation' OR last_error IS NOT NULL;
        CREATE INDEX archive_intake_dirty ON normalization.archive_item_intake(published_artist_uuid)
            WHERE catalog_revision > rebuilt_revision;

        CREATE VIEW normalization.archive_artist_review AS
        SELECT c.*, counts.item_count, counts.eligible_count,
            counts.first_performance_date, counts.last_performance_date,
            reason_counts.by_reason,
            evidence.item_identifier AS example_item, evidence.provider_url AS example_provider_url,
            evidence.payload AS example_metadata,
            representatives.items AS representative_items
        FROM normalization.archive_artist_candidates c
        LEFT JOIN LATERAL (
            SELECT count(*) AS item_count,
                count(*) FILTER (WHERE i.status IN ('awaiting_artist_approval','eligible','published')) AS eligible_count,
                min(i.interpretation->>'DisplayDate') AS first_performance_date,
                max(i.interpretation->>'DisplayDate') AS last_performance_date
            FROM normalization.archive_item_intake i WHERE i.candidate_id = c.candidate_id
        ) counts ON true
        LEFT JOIN LATERAL (
            SELECT o.item_identifier, o.provider_url, o.payload FROM normalization.archive_item_observations o
            WHERE o.observation_id = ANY(c.representative_observation_ids)
            ORDER BY o.observed_at DESC LIMIT 1
        ) evidence ON true
        LEFT JOIN LATERAL (
            SELECT jsonb_object_agg(reason,total) AS by_reason FROM (
                SELECT COALESCE(i.reason,i.status) AS reason,count(*) AS total
                FROM normalization.archive_item_intake i WHERE i.candidate_id=c.candidate_id
                GROUP BY COALESCE(i.reason,i.status)
            ) grouped_reasons
        ) reason_counts ON true
        LEFT JOIN LATERAL (
            SELECT jsonb_agg(jsonb_build_object('item_identifier',o.item_identifier,
                'provider_url',o.provider_url,'creator',o.payload->'metadata'->'creator')) AS items
            FROM normalization.archive_item_observations o
            WHERE o.observation_id=ANY(c.representative_observation_ids)
        ) representatives ON true;
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Keep intake evidence and ownership on code rollback. Removal requires a reviewed data migration."
        );
}
