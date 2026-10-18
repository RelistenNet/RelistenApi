namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    // People are bounded after redirects, so repeated assertions, spelling variants and
    // retired originals cannot consume another person's place. Threshold matches are read
    // in full; this bounds returned identities, not rows visited by the database.
    private const string PeopleNameQuery = """
        SELECT hits.origin_uuid AS "OriginUuid",hits.score AS "Score",hits.reason AS "Reason",
            hits.pool_truncated AS "PoolTruncated"
        FROM unnest(@names::text[]) input(name)
        CROSS JOIN LATERAL (
            WITH RECURSIVE original_scores AS MATERIALIZED (
                SELECT origin_uuid,
                    max(score) FILTER (WHERE reason='name') AS name_score,
                    max(score) FILTER (WHERE reason='alias') AS alias_score
                FROM (
                    SELECT p.uuid AS origin_uuid,
                        strict_word_similarity(input.name,p.name_key)::double precision AS score,
                        'name'::text AS reason
                    FROM normalization.people p
                    WHERE input.name <<% p.name_key
                    UNION ALL
                    SELECT a.person_uuid AS origin_uuid,
                        strict_word_similarity(input.name,a.name_key)::double precision AS score,
                        'alias'::text AS reason
                    FROM normalization.current_person_aliases a
                    WHERE input.name <<% a.name_key
                ) matching_names
                GROUP BY origin_uuid
            ), paths(original_uuid,uuid) AS (
                SELECT origin_uuid,origin_uuid FROM original_scores
                UNION
                SELECT p.original_uuid,r.survivor_uuid FROM paths p
                JOIN normalization.person_redirects r ON r.retired_uuid=p.uuid
            ), terminal_scores AS MATERIALIZED (
                SELECT p.uuid,max(o.name_score) AS name_score,max(o.alias_score) AS alias_score
                FROM paths p JOIN original_scores o ON o.origin_uuid=p.original_uuid
                WHERE NOT EXISTS (
                    SELECT 1 FROM normalization.person_redirects r WHERE r.retired_uuid=p.uuid
                )
                GROUP BY p.uuid
            ), bounded AS MATERIALIZED (
                SELECT uuid,name_score,alias_score FROM terminal_scores
                ORDER BY greatest(name_score,alias_score) DESC,uuid
                LIMIT @take
            ), counted AS (
                SELECT *,count(*) OVER()>=@take AS pool_truncated FROM bounded
            )
            SELECT c.uuid AS origin_uuid,reasons.score,reasons.reason,c.pool_truncated
            FROM counted c
            CROSS JOIN LATERAL (
                VALUES ('name'::text,c.name_score),('alias'::text,c.alias_score)
            ) reasons(reason,score)
            WHERE reasons.score IS NOT NULL
        ) hits
        """;
}
