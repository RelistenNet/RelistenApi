BEGIN READ ONLY;
SET LOCAL statement_timeout='20s';
EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON)
SELECT s.uuid::text,s.display_date FROM shows s JOIN artists a ON a.id=s.artist_id
WHERE a.uuid='77a58ff9-2e01-c59c-b8eb-cff106049b72'
ORDER BY s.display_date COLLATE "C" DESC,s.uuid::text COLLATE "C"
LIMIT 11;
ROLLBACK;
