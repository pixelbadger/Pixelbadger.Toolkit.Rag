# PBRAG Search Mode Evaluation

## Evaluation Scope

**This project is an MCP server.** We provide search capabilities; the LLM client uses them.

**What we evaluate:**
- Document retrieval quality (precision, recall)
- Search latency (index → results)
- Per-query costs (API calls this server makes)
- Storage/indexing costs

**What we DON'T evaluate:**
- LLM query formulation (happens client-side)
- LLM result synthesis (happens client-side)
- LLM inference costs (client's responsibility)
- End-to-end RAG latency (client orchestrates this)

**Cost accounting:** When we say "cost per query", we mean the cost of executing the search operation, NOT the cost of the LLM using that search. Vector search costs $0.0001/query because we call the embedding API. BM25 costs $0 because it's local. What the LLM client does with results is out of scope.

---

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

### Key Findings

**1. Vector Search Provides Excellent Semantic Understanding**
- "How do people react to cosmic entities?" → Found "two perished of pure fright" passage
- "Strange colors otherworldly phenomena" → Found "colour out of space" descriptions
- Query phrasing (natural vs keywords) doesn't matter for quality

**2. Hybrid Search Combines Best of Both Worlds**
- Gets exact matches from BM25 + semantic results from vector
- Same cost as vector-only (~$0.10 per 1K queries)
- Most robust across all query types

---

## Cost Analysis

### Per-Query Economics (MCP Server Perspective)

**BM25 Search:**
```
Lucene index lookup: $0/query
No external API calls, pure local computation
```

**Vector Search:**
```
Embedding API call: $0.0001/query (text-embedding-3-large)
Pre-computed chunk embeddings at index time
Query embedding generated per search
```

**Hybrid Search:**
```
BM25 lookup + Vector lookup: $0.0001/query
Same cost as vector-only (BM25 is free)
```

### At Scale (1M queries)

| Approach | Total Cost | Latency |
|----------|-----------|---------|
| BM25 | $0 | ~10ms |
| Vector | $100 | ~650ms |
| Hybrid | $100 | ~650ms |

**Note:** LLM costs (query formulation, result synthesis) are NOT included - those happen client-side and are not this MCP server's responsibility.

---

## Recommendations

### MCP Server Search Mode Selection

This is an MCP server - the LLM client is responsible for query formulation and result synthesis. Our job is to return relevant documents efficiently.

**✅ Default: Hybrid Search (BM25 + Vector)**
- Best retrieval quality across all query types
- $0.10 per 1K queries
- 650ms average latency
- Handles both precise term matching and semantic similarity

**✅ Technical Documentation: BM25 Only**
- 10ms latency (65x faster than hybrid)
- Zero query cost
- Excellent for reference docs with precise terminology
- LLM clients already formulate keyword-rich queries

**✅ Natural Language Content: Vector or Hybrid**
- Better for narrative content with varied phrasing
- Embeddings capture semantic relationships
- Use when exact terminology varies across documents

### Architectural Boundary

**MCP Server responsibilities (us):**
- Index documents efficiently
- Return relevant chunks for search queries
- Provide fast, low-cost retrieval

**LLM Client responsibilities (not us):**
- Understand user intent
- Formulate search queries
- Synthesize results into answers
- Multi-hop reasoning

**Cost accounting:** LLM inference costs are the client's concern. We optimize for retrieval quality, latency, and per-query search costs only.

---

## When to Choose Each Search Mode

### Use BM25 when:
- Queries contain precise technical terms
- Content uses standardized terminology
- Speed is critical (<10ms latency needed)
- Cost must be zero (high query volume)
- LLM clients formulate keyword-rich queries

### Use Vector when:
- Content uses varied phrasing for same concepts
- Semantic similarity matters more than exact matches
- Natural language queries from end users
- Cross-document conceptual relationships important

### Use Hybrid when:
- Mixed query types (both precise and semantic)
- Maximum recall required
- Moderate latency acceptable (~650ms)
- Budget allows $0.10 per 1K queries

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

**Default recommendation: Hybrid search**
- Best retrieval quality across query types
- Reasonable cost ($0.10 per 1K queries)
- 650ms latency acceptable for most use cases

**For technical documentation with LLM clients: Consider BM25-only**
- 65x faster (10ms vs 650ms)
- Zero cost
- Equivalent quality when LLM formulates keyword-rich queries
- See `docs/bm25-vs-hybrid-analysis.md` for detailed analysis

**This is an MCP server.** Focus on fast, accurate document retrieval. Let LLM clients handle query formulation and result synthesis.
