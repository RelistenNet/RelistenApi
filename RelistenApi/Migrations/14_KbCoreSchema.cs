using SimpleMigrations;

namespace Migrations;

[Migration(14, "Create the kb core (entities, claims) and the event projections reality reads")]
public sealed class KbCoreSchema : SqlFileMigration
{
    protected override void Up()
    {
        ExecuteSqlFile("014_kb_core.sql");

        // The two predicates the reality pipeline needs (docs/catalogue.md §Events vs shows).
        // WS-5 seeds the rest of the registry.
        Execute(
            """
            INSERT INTO kb.predicates (subject_type, name, schema_version, value_schema, risk)
            VALUES
                ('source', 'event', 1, '{
                    "type": "object",
                    "required": ["event"],
                    "additionalProperties": false,
                    "properties": {
                        "event": {"type": "string", "format": "uuid"},
                        "md5s": {"type": "array", "minItems": 1,
                                 "items": {"type": "string", "pattern": "^[0-9a-f]{32}$"}}
                    }
                }', 'structural'),
                ('event', 'legacy_show', 1, '{
                    "type": "object",
                    "required": ["show"],
                    "additionalProperties": false,
                    "properties": {"show": {"type": "string", "format": "uuid"}}
                }', 'structural');
            """
        );
    }
}
