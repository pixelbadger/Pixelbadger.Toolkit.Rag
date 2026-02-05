# PBRAG Search Mode Evaluation

## Test Methodology

**Corpus:** 2 H.P. Lovecraft short stories (~180KB text)
- The Call of Cthulhu
- The Colour Out of Space

**Index Configuration:**
- Semantic chunking with vector embeddings
- OpenAI `text-embedding-3-small` model
- Lucene.NET BM25 index
- 251 total chunks indexed

**Test Queries:**
- Natural language queries: "What are the ancient beings that sleep beneath the ocean?"
- Keyword queries: "ancient beings sleep ocean"
- Conceptual queries: "How do people react when encountering cosmic entities?"
- Optimized keyword queries: "fright fear terror sailors perished"
- Exact term queries: "Cthulhu R'lyeh"

---

## Results Summary

### Performance Comparison

| Mode | Avg Latency | Cost per 1K Queries | Semantic Quality | Exact Match Quality |
|------|-------------|---------------------|------------------|---------------------|
| **BM25** | ~10ms | $0 | ★★☆☆☆ | ★★★★★ |
| **Vector** | ~650ms | $0.10 | ★★★★★ | ★★★★☆ |
| **Hybrid** | ~650ms | $0.10 | ★★★★★ | ★★★★★ |
| **LLM→BM25** | ~1.5s | $3.00 | ★★★★★ | ★★★★★ |

### Key Findings

**1. Vector Search Provides Excellent Semantic Understanding**
- "How do people react to cosmic entities?" → Found "two perished of pure fright" passage
- "Strange colors otherworldly phenomena" → Found "colour out of space" descriptions
- Query phrasing (natural vs keywords) doesn't matter for quality

**2. BM25 with LLM Keyword Extraction Matches Vector Quality**
- LLM can reformulate queries into optimal keywords
- "fright fear terror sailors perished" (LLM-optimized) matches vector semantic results
- BUT: 30x more expensive than embeddings ($3.00 vs $0.10 per 1K queries)

**3. Hybrid Search Combines Best of Both Worlds**
- Gets exact matches from BM25 + semantic results from vector
- Same cost as vector-only (~$0.10 per 1K queries)
- Most robust across all query types

---

## Cost Analysis

### Per-Query Economics

**Vector Search:**
```
Embedding API call: $0.0001/query
Pre-computed intelligence, reused for every query
```

**LLM Keyword Extraction:**
```
LLM inference: $0.003/query (Sonnet 4.5)
Per-query intelligence, paid every time
```

### At Scale (1M queries)

| Approach | Total Cost | Why |
|----------|-----------|-----|
| Vector | $100 | Embeddings are cheap, pre-computed |
| LLM→BM25 | $3,000 | LLM inference per query is expensive |
| Hybrid | $100 | Same as vector |

**Conclusion:** Embeddings are the "compiled" version of semantic understanding. Pre-computation beats per-query LLM calls.

---

## Recommendations

### For RAG Applications with LLM Backend

**✅ Default: Hybrid Search (BM25 + Vector)**
- Best quality across all query types
- Reasonable cost ($0.10 per 1K queries)
- 650ms average latency
- Let LLM focus on synthesis, not keyword extraction

**✅ Cost-Optimized: Vector Only**
- Excellent semantic quality
- 30x cheaper than LLM keyword extraction
- Use when semantic understanding is primary need

**✅ Speed-Critical: BM25 Only**
- 10ms latency
- Zero query cost
- Use for autocomplete, previews, high-frequency exact-match queries

**✅ No-Index: LLM + BM25**
- Acceptable for low-volume applications (<100 queries/day)
- Use when you can't or won't build embeddings
- Higher per-query cost but zero index cost

### LLM Role in RAG Should Be:
1. Understanding user intent
2. Ranking and synthesizing search results
3. Multi-hop reasoning
4. Answer generation

**NOT** keyword extraction - embeddings handle this cheaper and faster.

---

## When LLM Keyword Extraction Makes Sense

Valid use cases exist:
- **Domain-specific reformulation:** "myocardial infarction" → "heart attack"
- **Query expansion:** Generate synonym variants
- **Filtering/faceting:** Extract structured search constraints
- **Temporal queries:** "Recent research" → add date filters
- **Iterative refinement:** Multi-step search based on results

---

## Example Queries

### Excellent Vector/Hybrid Results:
```bash
# Semantic concept understanding
pbrag query --index-path ./index --search-mode hybrid \
  --query "How do people react when encountering cosmic entities?"

# Thematic discovery
pbrag query --index-path ./index --search-mode vector \
  --query "strange colors and otherworldly phenomena"
```

### Excellent BM25 Results:
```bash
# Exact term matching
pbrag query --index-path ./index --search-mode bm25 \
  --query "Cthulhu R'lyeh"

# Fast autocomplete
pbrag query --index-path ./index --search-mode bm25 \
  --query "ancient beings" --max-results 10
```

---

## Bottom Line

**For production RAG with LLMs: Use Hybrid search.**
- 30x cheaper than LLM keyword extraction
- Faster (650ms vs 1.5s)
- Equal or better quality
- Let the LLM do what it's good at: reasoning and synthesis, not search query optimization
