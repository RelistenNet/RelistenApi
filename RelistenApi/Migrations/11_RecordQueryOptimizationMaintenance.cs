using System;
using SimpleMigrations;

namespace Migrations;

[Migration(11, "Record manually applied query optimization maintenance")]
public sealed class RecordQueryOptimizationMaintenance : Migration
{
    protected override void Up()
    {
        // Production DDL is applied directly from
        // Migrations/Manual/2026-07-07-query-optimization.sql so concurrent
        // index operations and operator checkpoints remain outside a migration transaction.
    }

    protected override void Down()
    {
        throw new NotSupportedException(
            "Query optimization maintenance must be rolled back with the reviewed manual SQL procedure.");
    }
}
