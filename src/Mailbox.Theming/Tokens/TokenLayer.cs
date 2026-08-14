namespace Mailbox.Theming.Tokens;

/// <summary>
/// The three layers a token can live in. Cascading is one-directional: primitives feed
/// semantics feed components. Overriding a primitive propagates everywhere; overriding a
/// component token is surgical.
/// </summary>
/// <remarks>
/// A theme author never has to know this exists. Declaring an accent colour is a complete,
/// valid theme — the engine derives the rest. The layers are for people who want to reach in.
/// </remarks>
public enum TokenLayer
{
    /// <summary>Raw values: palette ramps, type scale, spacing scale, radii, durations.</summary>
    Primitive,

    /// <summary>Roles: <c>surface.ground</c>, <c>text.primary</c>, <c>accent.rest</c>.</summary>
    Semantic,

    /// <summary>Specific parts: <c>ribbon.tab.selected.background</c>.</summary>
    Component,
}

public static class TokenLayerExtensions
{
    /// <summary>
    /// Infers the layer from a token key's leading segment. Keeps theme files from having to
    /// declare a layer per entry — <c>palette.blue.60</c> is obviously primitive.
    /// </summary>
    public static TokenLayer InferLayer(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var area = key.AsSpan(0, key.IndexOf('.') is var i and >= 0 ? i : key.Length);

        return area switch
        {
            "palette" or "type" or "space" or "radius" or "border" or "elevation" or "motion"
                => TokenLayer.Primitive,
            "surface" or "text" or "accent" or "state" or "status" or "focus"
                => TokenLayer.Semantic,
            _ => TokenLayer.Component,
        };
    }
}
