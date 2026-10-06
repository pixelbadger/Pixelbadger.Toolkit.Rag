using Pixelbadger.Toolkit.Rag.Embeddings.Onnx;

namespace Pixelbadger.Toolkit.Rag.Embeddings.Vision;

// STUB (Phase 0). Implemented by workstream B.
public sealed class VisionEncoder : IVisionEncoder
{
    public VisionEncoder(OnnxSessionProvider sessions) { }

    public EncodedFeatures Encode(PreprocessedImage image) => throw new NotImplementedException();
}
