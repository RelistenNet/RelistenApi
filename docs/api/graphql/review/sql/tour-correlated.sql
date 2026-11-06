BEGIN READ ONLY;
SET LOCAL statement_timeout='20s';
EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON)
SELECT t.uuid FROM tours t JOIN artists a ON a.id=t.artist_id
WHERE t.name ILIKE '%summer%'
ORDER BY (SELECT count(*) FROM shows s WHERE s.tour_id=t.id) DESC,
 t.start_date DESC,t.name COLLATE "C",t.uuid LIMIT 20;
ROLLBACK;
