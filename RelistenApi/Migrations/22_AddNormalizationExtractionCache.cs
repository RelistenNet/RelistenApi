using System;
using SimpleMigrations;

namespace Migrations;

/// <summary>Paid requests survive downstream failures and reserve uncertain costs before network I/O.</summary>
[Migration(22, "Retain normalization extraction cache and bounded spend ledger")]
public sealed class AddNormalizationExtractionCache : Migration
{
    public const string SchemaSql = """
        CREATE TABLE normalization.extraction_jobs (
            uuid uuid PRIMARY KEY,
            cap_usd numeric NOT NULL CHECK(cap_usd>0),
            accounted_usd numeric NOT NULL DEFAULT 0 CHECK(accounted_usd>=0),
            created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            CHECK(accounted_usd<=cap_usd)
        );
        CREATE TABLE normalization.extraction_requests (
            sha256 text PRIMARY KEY CHECK(length(sha256)=64),
            rendered_request text NOT NULL,
            response_json text,
            completed_at timestamptz,
            created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            CHECK((response_json IS NULL)=(completed_at IS NULL))
        );
        CREATE TABLE normalization.extraction_attempts (
            uuid uuid PRIMARY KEY,
            request_sha256 text NOT NULL REFERENCES normalization.extraction_requests(sha256),
            job_uuid uuid NOT NULL REFERENCES normalization.extraction_jobs(uuid),
            state text NOT NULL CHECK(state IN ('running','succeeded','uncertain','failed')),
            lease_until timestamptz NOT NULL,
            reserved_usd numeric NOT NULL CHECK(reserved_usd>0),
            actual_usd numeric CHECK(actual_usd>=0 AND actual_usd<=reserved_usd),
            input_tokens bigint CHECK(input_tokens>=0),
            output_tokens bigint CHECK(output_tokens>=0),
            provider_request_id text,
            reason text,
            created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            completed_at timestamptz
        );
        CREATE UNIQUE INDEX extraction_single_running_request ON normalization.extraction_attempts(request_sha256)
            WHERE state='running';
        CREATE INDEX extraction_attempts_job ON normalization.extraction_attempts(job_uuid);
        """;

    protected override void Up() => Execute(SchemaSql);

    protected override void Down() =>
        throw new NotSupportedException(
            "Keep paid extraction receipts on rollback; stop workers instead."
        );
}
