using System.Runtime.InteropServices;
using Avalonia;
using Mailbox.Core.Diagnostics;
using Mailbox.Theming.Palettes;

namespace Mailbox.App.Theming;

/// <summary>
/// The application's half of the palette picker: the desktop's scheme read from the desktop's
/// own file, a scheme sampled from the reader's chosen image, and the startup door the harness
/// drives. The mapping itself lives in the theming project; this is only where the pixels and
/// the paths are.
/// </summary>
internal static class PaletteDoor
{
    /// <summary>Writes and logs the named palette's theme at startup: a curated id, or <c>desktop</c>.</summary>
    internal const string Variable = "MAILBOX_PALETTE";

    internal static void RunIfAsked(string themesDirectory)
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } wanted) return;

        var scheme = string.Equals(wanted, "desktop", StringComparison.OrdinalIgnoreCase)
            ? DesktopScheme()
            : ColourSchemes.Find(wanted);
        if (scheme is null)
        {
            Log.Warn($"Harness: no palette \"{wanted}\" — this build has "
                     + string.Join(", ", ColourSchemes.Curated.Select(s => s.Id)) + ", desktop.");
            return;
        }

        var (result, path) = PaletteThemes.Write(scheme, themesDirectory);
        Log.Info($"Harness: palette — \"{scheme.Name}\" wrote \"{result.File.Id}\" based on {result.BaseId} "
                 + $"({result.TokensWritten.Count} token(s), {result.Repaired.Count} repaired, "
                 + $"{result.Residual.Count} residual) to {path}.");
    }

    /// <summary>The desktop's colour scheme, from its own configuration; null when there is none to read.</summary>
    internal static ColourScheme? DesktopScheme()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(config))
        {
            config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        var path = Path.Combine(config, "kdeglobals");
        try
        {
            return File.Exists(path) ? ColourSchemes.FromKde(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"The desktop's colour scheme could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// A scheme sampled from an image: decoded small, its pixels handed to the clustering.
    /// Null when the file does not decode.
    /// </summary>
    internal static ColourScheme? SchemeFromImage(string absolutePath)
    {
        try
        {
            using var stream = File.OpenRead(absolutePath);
            using var small = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 64);

            var size = small.PixelSize;
            var stride = size.Width * 4;
            var buffer = new byte[stride * size.Height];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                small.CopyPixels(new PixelRect(0, 0, size.Width, size.Height),
                    handle.AddrOfPinnedObject(), buffer.Length, stride);
            }
            finally
            {
                handle.Free();
            }

            var pixels = new List<(byte R, byte G, byte B)>(size.Width * size.Height);
            for (var i = 0; i + 3 < buffer.Length; i += 4)
            {
                if (buffer[i + 3] < 128) continue; // transparent pixels say nothing about colour
                pixels.Add((buffer[i + 2], buffer[i + 1], buffer[i]));
            }

            return ColourSchemes.FromPixels(pixels);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Warn($"No palette could be read from \"{absolutePath}\": {ex.Message}");
            return null;
        }
    }
}
