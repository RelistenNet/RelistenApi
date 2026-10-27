using SimpleMigrations;

namespace Migrations;

[Migration(
    13,
    "Create the norm song catalogue: songs, aliases, evidence, track songs, runs, change log"
)]
public sealed class NormSchema : SqlFileMigration
{
    protected override void Up()
    {
        // The DDL ships with Relisten.Normalization (string-theory ddl/002_norm.sql, verbatim).
        Execute(Relisten.Normalization.NormalizationSql.Read("013_norm.sql"));

        // Bands join the norm catalogue one at a time: true once an expert has approved the
        // artist's catalogue. The per-artist API cutover and any automated re-run only touch
        // approved artists; building a catalogue by hand works for any artist.
        Execute("ALTER TABLE features ADD COLUMN norm_catalogue boolean NOT NULL DEFAULT false");

        // The write side, per artist: keep the artist's venues current in norm, by the free
        // match after every import and the nightly run for what it leaves. Rollout to every
        // artist is by turning this on in batches; reading stays on norm_catalogue.
        Execute("ALTER TABLE features ADD COLUMN norm_venues boolean NOT NULL DEFAULT false");
    }
}
