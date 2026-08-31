using Mailbox.Core.Diagnostics;
using Mailbox.Theming.Import;

namespace Mailbox.App.Theming;

/// <summary>
/// The application's half of a theme import: the real image decoder — nothing from a
/// stranger's package lands on disk with its original bytes — and the startup door the
/// harness drives.
/// </summary>
internal static class ThemeImportDoor
{
    /// <summary>Imports the named package at startup, before the library loads, so the run can apply it.</summary>
    internal const string Variable = "MAILBOX_THEME_IMPORT";

    /// <summary>Adds the full mapping table to the read-back, one line per token.</summary>
    internal const string DumpVariable = "MAILBOX_THEME_IMPORT_DUMP";

    /// <summary>Decode whatever arrived, emit PNG; null for anything the decoder rejects — SVG included, for now.</summary>
    internal static byte[]? Reencode(byte[] source)
    {
        try
        {
            using var input = new MemoryStream(source);
            using var image = new Avalonia.Media.Imaging.Bitmap(input);
            using var output = new MemoryStream();
            image.Save(output, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or NullReferenceException)
        {
            return null;
        }
    }

    /// <summary>
    /// The harness door: <c>MAILBOX_THEME_IMPORT=&lt;path&gt;</c> runs one import through
    /// exactly the machinery the Options button will press, and logs the same report. The
    /// theme is then applied by naming it in <c>MAILBOX_THEME</c>, as any theme is.
    /// </summary>
    internal static void RunIfAsked(string themesDirectory)
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } path) return;

        try
        {
            var outcome = ImportedThemes.Import(path, themesDirectory, Reencode);
            foreach (var line in ImportReport.Lines(outcome)) Log.Info($"Harness: theme import — {line}");

            if (Environment.GetEnvironmentVariable(DumpVariable) is { Length: > 0 })
            {
                foreach (var line in ImportReport.Dump(outcome)) Log.Info($"Harness: theme import map — {line}");
            }
        }
        catch (Exception ex) when (ex is BrowserThemeException or Mailbox.Theming.Files.ThemeFileException
                                       or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Harness: theme import refused — {ex.Message}");
        }
    }
}
