namespace Aeshnidae.Enlightenment;

/// <summary>
/// Enlightenment's page in the Aeshnidae Codex: the same progress report /enlighten
/// gives, as a page. Found by Aeshnidae.Codex through reflection (a static class
/// called CodexPage with Title and Pages(Player)), so nothing here references that mod.
/// </summary>
public static class CodexPage
{
    public static string Title => "Enlightenment";

    public static string[] Pages(Player player)
    {
        if (player is null)
            return Array.Empty<string>();

        var sb = new StringBuilder();
        sb.AppendLine("ENLIGHTENMENT");
        sb.AppendLine();

        // Describe pads its columns for chat; the book font is proportional, so the
        // padding is squeezed out and the lines read as "label: value".
        foreach (var raw in Requirements.Describe(player).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
                continue;

            var collapsed = System.Text.RegularExpressions.Regex.Replace(trimmed, @"\s{2,}", " ");
            sb.AppendLine(line.StartsWith("  ") ? "  " + collapsed : collapsed);
        }

        sb.AppendLine();
        sb.AppendLine("Enlightening keeps your society, auras, luminance and unspent experience; it resets level, attributes and skills. Mastery survives it.");
        sb.AppendLine("Each enlightenment makes your personal dungeon copies harder and richer.");

        return new[] { sb.ToString().TrimEnd() };
    }
}
