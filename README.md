# Pixelbadger.Toolkit.Rag

A CLI toolkit for RAG (Retrieval-Augmented Generation) workflows, providing BM25 search indexing, querying, semantic chunking, and MCP server functionality powered by Lucene.NET.

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

Ingest content files into a Lucene.NET search index with intelligent chunking.

**Usage:**
```bash
pbrag ingest --index-path <index-directory> --content-path <content-file>
```

**Options:**
- `--index-path`: Path to the Lucene.NET index directory (required)
- `--content-path`: Path to the content file to ingest (required)
- `--chunking-strategy`: Chunking strategy to use: `semantic`, `markdown`, or `paragraph` (optional, default: auto-detect)

**Examples:**
```bash
# Ingest a text document (auto-detect chunking strategy)
pbrag ingest --index-path ./search-index --content-path document.txt

# Ingest a markdown file with explicit markdown chunking
pbrag ingest --index-path ./search-index --content-path readme.md --chunking-strategy markdown

# Ingest with semantic chunking (requires OPENAI_API_KEY environment variable)
export OPENAI_API_KEY="sk-..."
pbrag ingest --index-path ./search-index --content-path document.txt --chunking-strategy semantic

# Build an index from multiple files
pbrag ingest --index-path ./search-index --content-path doc1.txt
pbrag ingest --index-path ./search-index --content-path doc2.md
pbrag ingest --index-path ./search-index --content-path doc3.txt
```

**Details:**
- Automatically detects file type and applies appropriate chunking strategy
- Markdown files (.md): Header-based chunking preserving document structure
- Text files (.txt): Paragraph-based chunking splitting on double newlines
- Creates index directory if it doesn't exist
- Appends to existing index, allowing incremental ingestion
- Each chunk is indexed with source file, paragraph number, and unique source ID

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

## Technical Details

### BM25 Similarity Search

BM25 (Best Matching 25) is a probabilistic ranking function used to estimate the relevance of documents to a search query. This implementation:

- Uses Lucene.NET's `BM25Similarity` for scoring
- Ranks results by relevance, not just keyword matching
- Handles term frequency and inverse document frequency
- Provides normalized scores for result comparison
- Supports multi-term queries with boolean logic

### Content Chunking

Content is intelligently chunked using one of three strategies. By default, chunking strategy is auto-detected based on file type, but can be explicitly specified:

**Semantic Chunking (`--chunking-strategy semantic`):**
- **Embedding-based chunking** using OpenAI's text-embedding-3-large model
- Splits text into sentences and generates embeddings for each with buffer context
- Calculates cosine distances between consecutive sentence embeddings
- Identifies semantic breakpoints using percentile-based threshold detection (95th percentile default)
- Groups sentences into chunks where semantic coherence is highest
- **Requires OpenAI API key** (set via OPENAI_API_KEY environment variable)
- **Embeddings are discarded after chunking** (BM25 search doesn't use them)
- **Best for:** Complex documents where semantic boundaries don't align with structural markers

**Markdown Chunking (`--chunking-strategy markdown`, auto-detected for .md files):**
- Header-based chunking using markdown headers (# ## ### etc.)
- Preserves document structure and hierarchy
- Each section becomes a searchable chunk
- Maintains context within logical document divisions
- **Best for:** Well-structured markdown documentation

**Paragraph Chunking (`--chunking-strategy paragraph`, auto-detected for .txt files):**
- Paragraph-based chunking splitting on double newlines (`\n\n`)
- Preserves natural paragraph boundaries
- Suitable for prose, documentation, and unstructured text
- Maintains readability and context in search results
- **Best for:** Plain text documents with clear paragraph structure

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
