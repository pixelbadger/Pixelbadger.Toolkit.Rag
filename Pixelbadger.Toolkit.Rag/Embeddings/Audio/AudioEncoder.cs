using Microsoft.ML.OnnxRuntime;
using Pixelbadger.Toolkit.Rag.Embeddings.Onnx;

namespace Pixelbadger.Toolkit.Rag.Embeddings.Audio;

/// <summary>
/// Runs <c>audio_encoder_quantized.onnx</c>: <c>input_features</c> float32 [clips, frames, 128] and <c>input_features_mask</c> bool
/// [clips, frames] to <c>audio_features</c> float32 [tokens, 512] (valid tokens flattened across clips in order).
/// The session is loaded lazily by <see cref="OnnxSessionProvider"/>. [verify] untested against the real model.
/// </summary>
public sealed class AudioEncoder : IAudioEncoder
{
    public const string FeaturesInput = "input_features";
    public const string MaskInput = "input_features_mask";
    public const string FeaturesOutput = "audio_features";

    private readonly OnnxSessionProvider _sessions;

    public AudioEncoder(OnnxSessionProvider sessions)
    {
        _sessions = sessions;
    }

    public EncodedFeatures Encode(PreprocessedAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (audio.Clips <= 0 || audio.Frames <= 0)
            throw new ArgumentException("Audio must contain at least one clip and one frame.", nameof(audio));
        if (audio.InputFeatures.Length != (long)audio.Clips * audio.Frames * PreprocessedAudio.MelBins)
            throw new ArgumentException("InputFeatures length does not match Clips * Frames * 128.", nameof(audio));
        if (audio.Mask.Length != audio.Clips * audio.Frames)
            throw new ArgumentException("Mask length does not match Clips * Frames.", nameof(audio));

        var session = _sessions.Audio;

        using var features = OrtValue.CreateTensorValueFromMemory(
            audio.InputFeatures, [audio.Clips, audio.Frames, PreprocessedAudio.MelBins]);
        using var mask = OrtValue.CreateTensorValueFromMemory(audio.Mask, [audio.Clips, audio.Frames]);

        var inputs = new Dictionary<string, OrtValue> { [FeaturesInput] = features, [MaskInput] = mask };
        using var runOptions = new RunOptions();
        using var outputs = session.Run(runOptions, inputs, [FeaturesOutput]);

        var output = outputs[0];
        var shape = output.GetTensorTypeAndShape().Shape;
        if (shape.Length != 2 || shape[1] != EncodedFeatures.FeatureWidth)
            throw new InvalidOperationException(
                $"Unexpected {FeaturesOutput} shape [{string.Join(", ", shape)}]; expected [tokens, {EncodedFeatures.FeatureWidth}].");

        return new EncodedFeatures(output.GetTensorDataAsSpan<float>().ToArray(), (int)shape[0]);
    }
}
