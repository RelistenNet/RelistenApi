using System;
using SimpleMigrations;

namespace Migrations;

[Migration(12, "Record manually applied PostgreSQL follow-up maintenance")]
public sealed class RecordPostgresFollowupMaintenance : Migration
{
    protected override void Up()
    {
        // Production DDL is applied directly from
        // Migrations/Manual/2026-07-08-postgres-followup.sql so concurrent
        // index operations and operator checkpoints remain outside a migration transaction.
    }

    protected override void Down()
    {
        throw new NotSupportedException(
            "PostgreSQL follow-up maintenance must be rolled back with the reviewed manual SQL procedure.");
    }
}
