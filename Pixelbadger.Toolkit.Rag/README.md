# Pixelbadger.Toolkit.Rag

A CLI toolkit for RAG (Retrieval-Augmented Generation) workflows, providing BM25 and vector similarity search indexing, querying, content-aware chunking (paragraph and markdown), and MCP server functionality powered by Lucene.NET and sqlite-vec.

## Table of Contents

- [Installation](#installation)
- [Usage](#usage)
- [Available Commands](#available-commands)
  - [ingest](#ingest)
  - [query](#query)
  - [serve](#serve)
- [MCP Server Integration](#mcp-server-integration)
- [Help](#help)
- [Requirements](#requirements)
- [Technical Details](#technical-details)
  - [BM25 Similarity Search](#bm25-similarity-search)
  - [Content Chunking](#content-chunking)
  - [Index Structure](#index-structure)

## Installation

### Option 1: Install as .NET Global Tool (Recommended)

Install the tool globally using the NuGet package:
```bash
dotnet tool install --global Pixelbadger.Toolkit.Rag
```

Once installed, you can use the `pbrag` command from anywhere:
```bash
pbrag --help
```

### Option 2: Build from Source

Clone the repository and build the project:
```bash
git clone https://github.com/pixelbadger/Pixelbadger.Toolkit.git
cd Pixelbadger.Toolkit/Pixelbadger.Toolkit.Rag
dotnet build
```

## Usage

### Using the Global Tool (pbrag)
Run commands using the flat command pattern:
```bash
pbrag [command] [options]
```

### Using from Source
If building from source, use:
```bash
dotnet run -- [command] [options]
```

## Available Commands

### ingest

Ingest content files into dual search indexes (Lucene BM25 + SQLite-vec) with content-aware chunking.

**Usage:**
```bash
pbrag ingest --index-path <index-directory> --content-path <content-file>
```

**Options:**
- `--index-path`: Path to the Lucene.NET index directory (required)
- `--content-path`: Path to the content file or folder to ingest (required)

**Examples:**
```bash
# Set OpenAI API key (required for vector embeddings)
export OPENAI_API_KEY="sk-..."

# Ingest a single text document (uses paragraph chunking)
pbrag ingest --index-path ./search-index --content-path document.txt

# Ingest a markdown file (uses header-based chunking)
pbrag ingest --index-path ./search-index --content-path readme.md

# Ingest an entire folder
pbrag ingest --index-path ./search-index --content-path ./docs-folder

# Build an index from multiple files
pbrag ingest --index-path ./search-index --content-path doc1.txt
pbrag ingest --index-path ./search-index --content-path doc2.md
pbrag ingest --index-path ./search-index --content-path doc3.txt
```

**Details:**
- Uses content-aware chunking: paragraphs for .txt files, headers for .md files
- Supports both single files and folders (recursively processes all .txt and .md files)
- Automatically creates dual indexes: Lucene BM25 for keyword search and SQLite-vec for semantic search
- Creates index directory if it doesn't exist
- Appends to existing index, allowing incremental ingestion
- Each chunk is indexed with source file, chunk number, unique source ID, and vector embeddings
- Requires `OPENAI_API_KEY` environment variable for vector embedding generation

### query

Perform BM25 similarity search against a Lucene.NET index to find relevant content.

**Usage:**
```bash
pbrag query --index-path <index-directory> --query <search-query> [--max-results <number>] [--sourceIds <id1> <id2> ...]
```

**Options:**
- `--index-path`: Path to the Lucene.NET index directory (required)
- `--query`: Search query text (required)
- `--max-results`: Maximum number of results to return (optional, default: 10)
- `--sourceIds`: Optional list of source IDs to constrain search results (optional)
- `--search-mode`: Search mode to use: bm25, vector, or hybrid (optional, default: bm25)

**Examples:**
```bash
# Basic search query
pbrag query --index-path ./search-index --query "machine learning algorithms"

# Limit results to top 5
pbrag query --index-path ./search-index --query "neural networks" --max-results 5

# Search within specific source documents
pbrag query --index-path ./search-index --query "data processing" --sourceIds doc1.txt doc2.md

# Complex multi-word query
pbrag query --index-path ./search-index --query "how to implement dependency injection in C#"

# Vector similarity search (requires OPENAI_API_KEY environment variable)
export OPENAI_API_KEY="sk-..."
pbrag query --index-path ./search-index --query "machine learning algorithms" --search-mode vector

# Hybrid search combining BM25 and vector (requires OPENAI_API_KEY environment variable)
pbrag query --index-path ./search-index --query "neural networks" --search-mode hybrid
```

**Output Format:**
```
Found 3 result(s):

Result 1 (Score: 2.4531)
Source: document.txt (Paragraph 5)
Content: Machine learning algorithms are fundamental to modern AI systems...
------------------------------------------------------------
Result 2 (Score: 1.8923)
Source: readme.md (Paragraph 12)
Content: Neural networks represent a class of machine learning models...
------------------------------------------------------------
Result 3 (Score: 1.2451)
Source: guide.txt (Paragraph 3)
Content: The application of algorithms in machine learning has transformed...
```

### serve

Host an MCP (Model Context Protocol) server that performs BM25 queries against a Lucene.NET index, enabling AI assistants to search your indexed content.

**Usage:**
```bash
pbrag serve --index-path <index-directory>
```

**Options:**
- `--index-path`: Path to the Lucene.NET index directory (required)

**Examples:**
```bash
# Start MCP server for an existing index
pbrag serve --index-path ./search-index
```

**Details:**
- Runs as a stdio-based MCP server
- Exposes a search tool that AI assistants can call
- Uses BM25 similarity ranking for relevance
- Supports result filtering by source IDs
- Logs all activity to stderr for monitoring
- Ideal for integration with Claude Desktop or other MCP-compatible clients

## MCP Server Integration

The `serve` command implements the Model Context Protocol (MCP), allowing AI assistants to search your indexed content dynamically during conversations.

### Claude Desktop Configuration

Add the following to your Claude Desktop configuration:

**MacOS:** `~/Library/Application Support/Claude/claude_desktop_config.json`
**Windows:** `%APPDATA%/Claude/claude_desktop_config.json`

```json
{
  "mcpServers": {
    "rag-search": {
      "command": "pbrag",
      "args": ["serve", "--index-path", "/absolute/path/to/your/search-index"]
    }
  }
}
```

### MCP Tool Interface

The server exposes a single tool called `Execute` with the following parameters:

- **query** (required): The search query to be performed
- **maxResults** (optional): Maximum number of results to return (default: 5)
- **sourceIds** (optional): Array of source IDs to constrain search results to specific documents
- **searchMode** (optional): Search mode to use: "bm25", "vector", or "hybrid" (default: "bm25")

When an AI assistant uses this tool, it receives formatted search results including relevance scores, source files, paragraph numbers, and content excerpts.

### Workflow Example

```bash
# Step 1: Ingest your documentation
pbrag ingest --index-path ./docs-index --content-path ./api-docs.md
pbrag ingest --index-path ./docs-index --content-path ./tutorial.md
pbrag ingest --index-path ./docs-index --content-path ./reference.txt

# Step 2: Test queries locally
pbrag query --index-path ./docs-index --query "authentication"

# Step 3: Start MCP server (or configure in Claude Desktop)
pbrag serve --index-path ./docs-index
```

## Help

Get help for any command by adding `--help`:
```bash
pbrag --help                     # General help
pbrag ingest --help              # Command-specific help
pbrag query --help               # Command-specific help
pbrag serve --help               # Command-specific help
```

## Requirements

- .NET 9.0
- Lucene.NET 4.8.0 (beta)
- ModelContextProtocol 0.3.0 (for MCP server functionality)
- Microsoft.Extensions.VectorData.Abstractions 9.7.0 (for vector data abstractions)
- Microsoft.SemanticKernel.Connectors.SqliteVec 1.68.0-preview (for sqlite-vec vector storage)
- Microsoft.Extensions.AI (for embedding generation)

## Technical Details

### BM25 Similarity Search
### Vector Similarity Search

Vector similarity search complements BM25 with semantic understanding of content. This implementation:

- Uses OpenAI's `text-embedding-3-large` model (3072 dimensions) for generating embeddings
- Stores embeddings in a sqlite-vec database alongside the Lucene index
- Calculates cosine similarity between query embeddings and stored document embeddings
- Supports three search modes: pure vector search, BM25 keyword search, or hybrid search combining both with Reciprocal Rank Fusion (RRF)
- Requires `OPENAI_API_KEY` environment variable for embedding generation during ingest and queries
- Embeddings are generated during content ingestion and stored persistently for efficient querying
- Enables semantic search that understands meaning and context beyond keyword matching

### Content Chunking

The system uses content-aware chunking strategies tailored to each file type:

**Paragraph Chunking (.txt files)**
- Splits text on double newlines (`\n\n`, `\r\n\r\n`) to identify paragraph boundaries
- Falls back to single newlines if no double newlines are found
- Filters out empty or whitespace-only paragraphs
- Preserves the natural document structure without breaking mid-thought
- Each chunk receives a sequential chunk number

**Markdown Chunking (.md files)**
- Splits markdown documents by headers (H1-H6: `#` to `######`)
- Each chunk includes the header line and all content until the next header
- Preserves header hierarchy and context
- Handles content before the first header as a separate chunk
- Ideal for documentation where headers denote topic boundaries

**ChunkerFactory**
- Automatically selects the appropriate chunker based on file extension
- `.md` files → `MarkdownTextChunker`
- All other files (including `.txt`) → `ParagraphTextChunker`

**Benefits**
- Respects natural document structure instead of arbitrary token limits
- Preserves semantic coherence within chunks
- Simple, deterministic, and fast (no ML model required for chunking)
- Embeddings are generated during storage, not during chunking

### Index Structure

Each indexed chunk contains the following fields:

- **sourceFile**: Original file path for provenance
- **paragraphNumber**: Sequential chunk number within the source file
- **content**: The actual text content being indexed and searched
- **sourceId**: Unique identifier derived from the file path (used for filtering)

The index uses:
- **StandardAnalyzer**: For robust tokenization and normalization
- **BM25Similarity**: For relevance-based ranking
- **SimpleDirectory**: For persistent on-disk storage
- **Atomic writes**: Index updates are transactional and crash-safe
