# Vector Search Cost Analysis Presentation

This directory contains two versions of a presentation arguing against vector search for agentic search use cases in B2B enterprise environments.

## Files

1. **presentation.html** - Interactive HTML presentation (ready to use immediately)
2. **generate_powerpoint.py** - Python script to generate PowerPoint file
3. **vector-search-unnecessary-cost.pptx** - Generated PowerPoint file (after running script)

## Quick Start: HTML Presentation (Recommended)

The HTML presentation is ready to use immediately with no dependencies:

```bash
# Open in your default browser
xdg-open presentation.html   # Linux
open presentation.html        # macOS
start presentation.html       # Windows

# Or serve it locally
python3 -m http.server 8000
# Then navigate to: http://localhost:8000/presentation.html
```

### HTML Presentation Controls

- **Arrow keys**: Navigate slides (←/→ or ↑/↓)
- **Space**: Next slide
- **Esc**: Overview mode (see all slides)
- **F**: Fullscreen
- **S**: Speaker notes (if available)
- **?**: Show keyboard shortcuts

## PowerPoint Generation

To generate an actual PowerPoint file:

```bash
# Install dependency
pip install python-pptx

# Run the generator
python3 generate_powerpoint.py

# This creates: vector-search-unnecessary-cost.pptx
```

## Presentation Structure (12 Slides)

1. **Title Slide** - "Vector Search: Unnecessary Cost"
2. **Executive Summary** - Recommendation and key drawbacks
3. **Real-World Test Results** - Side-by-side comparison showing identical quality
4. **Cost Breakdown** - Detailed cost analysis table
5. **What is Agentic Search** - Explains the LLM-mediated search paradigm
6. **Why LLMs Make Vector Search Redundant** - The semantic translation insight
7. **Why BM25 Wins for Technical Docs** - Precise terminology matching
8. **Head-to-Head Performance** - Comprehensive metrics table
9. **When You Actually Need Vectors** - Valid use cases (none apply here)
10. **The Bottom Line** - Infinite ROI message
11. **Recommendations** - Immediate actions and monitoring
12. **Questions/Closing** - Key takeaways

## Target Audience

B2B enterprise software product teams evaluating search technology for:
- Technical documentation search
- Developer knowledge bases
- API reference materials
- Agentic/LLM-mediated search systems

## Key Arguments

1. **Quality**: BM25 and vector hybrid show identical accuracy (empirically tested)
2. **Cost**: BM25 is free vs. $2/year + API costs for vectors
3. **Performance**: BM25 is 50-130x faster (<10ms vs 500-1,300ms)
4. **Reliability**: BM25 has no external dependencies
5. **The Agentic Factor**: LLMs already perform semantic-to-keyword translation

## Customization

### HTML Presentation

Edit `presentation.html` and modify:
- Color scheme in the `<style>` section (CSS variables)
- Slide content in `<section>` tags
- Reveal.js settings in the `Reveal.initialize()` call

### PowerPoint Script

Edit `generate_powerpoint.py` and modify:
- Colors defined at the top of `create_presentation()`
- Slide content in each slide creation section
- Font sizes, positions using `Inches()` and `Pt()`

## Converting HTML to PowerPoint

If you prefer to edit the HTML and convert it:

**Option 1: Use reveal.js export**
- Open presentation.html in browser
- Press 'E' for export mode
- Print to PDF
- Use PDF to PowerPoint converter

**Option 2: Use decktape**
```bash
npm install -g decktape
decktape reveal presentation.html presentation.pdf
```

## Data Source

All data and arguments are based on:
- **docs/bm25-vs-hybrid-analysis.md** - Comprehensive cost-benefit analysis
- Real testing on Beej's Guide to Network Programming corpus
- Query: "Describe differences between Windows and Linux TCP/IP stacks"

## Presenter Notes

### Key Points to Emphasize

1. **This is data-driven**: Real tests showing identical quality
2. **Not anti-vector in general**: Vectors have valid use cases, just not this one
3. **The agentic factor is critical**: LLMs change the equation entirely
4. **Infinite ROI**: Same quality at zero cost = infinite return

### Anticipating Questions

**Q: "What about future-proofing?"**
A: Monitor metrics. Switch if query failure rate >5%. Current evidence: unlikely.

**Q: "Isn't $2/year negligible?"**
A: Cost is minor, but 50-130x latency degradation impacts user experience significantly.

**Q: "What if the corpus grows?"**
A: BM25 scales well. Only reconsider at 1000+ documents with inconsistent terminology.

**Q: "Don't modern systems use hybrid search?"**
A: For end-user search, yes. But agentic search is different—the LLM is your semantic layer.

## License

This presentation is based on analysis in this repository. Feel free to adapt for your organization.
