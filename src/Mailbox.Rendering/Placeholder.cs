using System.Globalization;
using System.Text;

namespace Mailbox.Rendering;

/// <summary>
/// What a blocked image is replaced with.
/// </summary>
/// <remarks>
/// Something visible rather than nothing. An image that silently disappears reads as a broken
/// message and sends people looking for a fault; a marked box reads as a decision, which is
/// what it is, and the InfoBar above says how to undo it.
/// <para>
/// Drawn as an SVG data URI so it scales to whatever size the message asked for and costs no
/// asset in the package. Its colour is passed in from the theme, like everything else.
/// </para>
/// </remarks>
internal static class Placeholder
{
    internal static string DataUri(RenderStyle style)
    {
        var stroke = style.Quote;

        var svg = new StringBuilder()
            .Append("<svg xmlns='http://www.w3.org/2000/svg' width='120' height='90' ")
            .Append("viewBox='0 0 120 90'>")
            .Append(CultureInfo.InvariantCulture, $"<rect x='0.5' y='0.5' width='119' height='89' fill='none' stroke='{stroke}' stroke-dasharray='4 3'/>")
            .Append(CultureInfo.InvariantCulture, $"<path d='M28 62 L50 38 L66 56 L76 46 L92 62 Z' fill='{stroke}' opacity='0.35'/>")
            .Append(CultureInfo.InvariantCulture, $"<circle cx='40' cy='30' r='6' fill='{stroke}' opacity='0.35'/>")
            .Append("</svg>")
            .ToString();

        return "data:image/svg+xml;base64,"
               + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    }
}
