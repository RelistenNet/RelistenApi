CREATE INDEX CONCURRENTLY IF NOT EXISTS source_reviews_search_trgm_idx
ON public.source_reviews USING gin ((coalesce(title || ' — ', '') || review) public.gin_trgm_ops);
