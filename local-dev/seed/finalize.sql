-- Derived counts describe the sample, not the full production catalog.
INSERT INTO show_source_information
    (show_id, max_updated_at, max_created_at, source_count, artist_id,
     max_avg_rating_weighted, has_soundboard_source, has_flac)
SELECT show_id, max(updated_at), max(created_at), count(*), artist_id,
       max(avg_rating_weighted), bool_or(is_soundboard), bool_or(flac_type IN (1, 2))
FROM sources GROUP BY show_id, artist_id;

INSERT INTO source_review_counts
SELECT source_id, max(updated_at), count(*) FROM source_reviews GROUP BY source_id;

INSERT INTO venue_show_counts
SELECT v.id, CASE WHEN count(DISTINCT src.show_id) > 0 THEN count(DISTINCT src.show_id)
                 ELSE count(DISTINCT sh.id) END
FROM venues v LEFT JOIN sources src ON src.venue_id = v.id
LEFT JOIN shows sh ON sh.venue_id = v.id GROUP BY v.id;

UPDATE years y SET show_count = aggregate.show_count, source_count = aggregate.source_count,
    duration = aggregate.duration, avg_duration = aggregate.avg_duration, avg_rating = aggregate.avg_rating
FROM (
    SELECT sh.year_id, count(DISTINCT sh.id) AS show_count, count(src.id) AS source_count,
           sum(src.duration) AS duration, avg(sh.avg_duration) AS avg_duration, avg(sh.avg_rating) AS avg_rating
    FROM shows sh JOIN sources src ON src.show_id = sh.id GROUP BY sh.year_id
) aggregate WHERE aggregate.year_id = y.id;

-- Preserve public IDs while making the next local insert safe.
DO $$
DECLARE item record;
BEGIN
    FOR item IN
        SELECT table_name, column_name,
               pg_get_serial_sequence(format('public.%I', table_name), column_name) AS sequence_name
        FROM information_schema.columns WHERE table_schema = 'public'
          AND column_default LIKE 'nextval(%'
    LOOP
        EXECUTE format('SELECT setval(%L, coalesce(max(%I), 1), max(%I) IS NOT NULL) FROM public.%I',
                       item.sequence_name, item.column_name, item.column_name, item.table_name);
    END LOOP;
END $$;
