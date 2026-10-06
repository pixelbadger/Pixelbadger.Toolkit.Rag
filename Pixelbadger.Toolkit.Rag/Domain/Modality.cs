namespace Pixelbadger.Toolkit.Rag.Domain;

/// <summary>
/// The kind of content a chunk was embedded from. Persisted as tinyint.
/// </summary>
public enum Modality : byte
{
    Text = 0,
    Image = 1,
    Audio = 2
}
