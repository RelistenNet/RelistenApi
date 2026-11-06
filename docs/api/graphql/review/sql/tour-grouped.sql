BEGIN READ ONLY;
SET LOCAL statement_timeout='20s';
EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON)
SELECT t.uuid FROM tours t JOIN artists a ON a.id=t.artist_id
LEFT JOIN shows s ON s.tour_id=t.id WHERE t.name ILIKE '%summer%'
GROUP BY t.id ORDER BY count(s.id) DESC,t.start_date DESC,t.name COLLATE "C",t.uuid LIMIT 20;
ROLLBACK;
