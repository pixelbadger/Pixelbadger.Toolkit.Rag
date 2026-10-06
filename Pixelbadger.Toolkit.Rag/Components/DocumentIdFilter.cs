namespace Pixelbadger.Toolkit.Rag.Components;

/// <summary>Parses the <c>documentIds</c> search filter that REST and MCP callers send as strings.</summary>
public static class DocumentIdFilter
{
    /// <summary>
    /// Null or empty means "no filter" (null is returned). Every entry must be a GUID.
    /// </summary>
    /// <exception cref="ArgumentException">An entry is not a GUID; the message names it.</exception>
    public static IReadOnlyList<Guid>? Parse(IReadOnlyCollection<string>? documentIds)
    {
        if (documentIds is not { Count: > 0 })
            return null;

        var parsed = new List<Guid>(documentIds.Count);
        foreach (var raw in documentIds)
        {
            if (!Guid.TryParse(raw, out var id))
                throw new ArgumentException($"documentIds entry '{raw}' is not a valid document id (a GUID).", nameof(documentIds));
            parsed.Add(id);
        }

        return parsed;
    }
}
