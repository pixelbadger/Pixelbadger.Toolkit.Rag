#!/usr/bin/env bash
# Downloads a Gemma 3 tokenizer.json (same 262,144-entry BPE vocabulary family as EmbeddingGemma 2) from the
# npm package @lenml/tokenizer-gemma3 and installs it where the tokenizer parity tests look for it:
#   Pixelbadger.Toolkit.Rag.Tests/test-assets/tokenizer/tokenizer.json   (git-ignored, never committed)
#
# Use this when you have no local EmbeddingGemma 2 snapshot (the tests prefer $PBRAG_MODEL_PATH/tokenizer.json,
# then $PBRAG_TOKENIZER_PATH, then this file). Needs: npm, tar.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dest="$here/../../Pixelbadger.Toolkit.Rag.Tests/test-assets/tokenizer"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

mkdir -p "$dest"
(cd "$work" && npm pack @lenml/tokenizer-gemma3@3.7.2 --silent >/dev/null && tar xzf ./*.tgz package/models/tokenizer.json)
cp "$work/package/models/tokenizer.json" "$dest/tokenizer.json"
echo "installed $dest/tokenizer.json ($(wc -c < "$dest/tokenizer.json") bytes)"
