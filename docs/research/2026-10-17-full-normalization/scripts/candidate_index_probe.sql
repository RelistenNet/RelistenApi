-- Local query-plan probe only. Synthetic names do not measure matching accuracy or
-- production throughput. Run with psql -X -v ON_ERROR_STOP=1 against a disposable database.
-- ROLLBACK removes the table and, if newly created, the extension.
BEGIN;
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE TEMP TABLE candidate_probe(uuid uuid NOT NULL, name_key text NOT NULL);
INSERT INTO candidate_probe
SELECT md5(i::text)::uuid,
    CASE WHEN i%10=0 THEN 'charlie miller ' ELSE 'recording taper ' END || md5((i%10000)::text)
FROM generate_series(1,100000) i;
CREATE INDEX candidate_probe_name ON candidate_probe USING gist(name_key gist_trgm_ops);
ANALYZE candidate_probe;
SET LOCAL pg_trgm.strict_word_similarity_threshold=.25;

-- UUID tie-breaking makes PostgreSQL read the complete equal-distance group.
EXPLAIN (ANALYZE, BUFFERS)
SELECT uuid, strict_word_similarity('charlie miller',name_key)::double precision
FROM candidate_probe
WHERE 'charlie miller' <<% name_key
ORDER BY 'charlie miller' <<<-> name_key,uuid
LIMIT 101;

-- The implementation bounds this name pool and reports truncation to its caller.
EXPLAIN (ANALYZE, BUFFERS)
SELECT uuid, strict_word_similarity('charlie miller',name_key)::double precision
FROM candidate_probe
WHERE 'charlie miller' <<% name_key
ORDER BY 'charlie miller' <<<-> name_key
LIMIT 101;
ROLLBACK;
