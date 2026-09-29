using System.Net;
using System.Text;

namespace CodexRelay;

internal static class TelegramMarkup
{
    public static string RenderAnswer(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var result = new StringBuilder();
        var inCode = false;
        var inQuote = false;
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (inQuote) { result.Append("</blockquote>\n"); inQuote = false; }
                if (inCode) result.Append("</pre>\n");
                else result.Append("<pre>");
                inCode = !inCode;
                continue;
            }
            if (inCode)
            {
                result.Append(WebUtility.HtmlEncode(line)).Append('\n');
                continue;
            }
            if (trimmed.StartsWith('>'))
            {
                if (!inQuote) { result.Append("<blockquote>"); inQuote = true; }
                result.Append(RenderInline(trimmed[1..].TrimStart())).Append('\n');
                continue;
            }
            if (inQuote) { result.Append("</blockquote>\n"); inQuote = false; }
            if (trimmed.Length == 0)
            {
                if (result.Length > 0 && result[^1] != '\n') result.Append('\n');
                result.Append('\n');
                continue;
            }
            var headingLength = 0;
            while (headingLength < trimmed.Length && headingLength < 6 && trimmed[headingLength] == '#')
                headingLength++;
            if (headingLength > 0 && headingLength < trimmed.Length && trimmed[headingLength] == ' ')
                result.Append("<b>").Append(RenderInline(trimmed[(headingLength + 1)..])).Append("</b>");
            else if (trimmed is "---" or "***" or "___")
                result.Append("────────────");
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
                trimmed.StartsWith("* ", StringComparison.Ordinal))
                result.Append("• ").Append(RenderInline(trimmed[2..]));
            else result.Append(RenderInline(line));
            result.Append('\n');
        }
        if (inQuote) result.Append("</blockquote>");
        if (inCode) result.Append("</pre>");
        return result.ToString().Trim();
    }

    private static string RenderInline(string text)
    {
        var result = new StringBuilder();
        for (var index = 0; index < text.Length;)
        {
            if (text[index] == '`')
            {
                var end = text.IndexOf('`', index + 1);
                if (end > index + 1)
                {
                    result.Append("<code>").Append(WebUtility.HtmlEncode(text[(index + 1)..end])).Append("</code>");
                    index = end + 1;
                    continue;
                }
            }
            if (index + 1 < text.Length && text[index] == '*' && text[index + 1] == '*')
            {
                var end = text.IndexOf("**", index + 2, StringComparison.Ordinal);
                if (end > index + 2)
                {
                    result.Append("<b>").Append(WebUtility.HtmlEncode(text[(index + 2)..end])).Append("</b>");
                    index = end + 2;
                    continue;
                }
            }
            if (text[index] == '[')
            {
                var labelEnd = text.IndexOf("](", index + 1, StringComparison.Ordinal);
                var linkEnd = labelEnd >= 0 ? text.IndexOf(')', labelEnd + 2) : -1;
                if (labelEnd > index + 1 && linkEnd > labelEnd + 2)
                {
                    var url = text[(labelEnd + 2)..linkEnd];
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                        uri.Scheme is "http" or "https")
                    {
                        result.Append("<a href=\"").Append(WebUtility.HtmlEncode(url)).Append("\">")
                            .Append(WebUtility.HtmlEncode(text[(index + 1)..labelEnd])).Append("</a>");
                        index = linkEnd + 1;
                        continue;
                    }
                }
            }
            var next = index + 1;
            while (next < text.Length && text[next] is not ('`' or '*' or '[')) next++;
            result.Append(WebUtility.HtmlEncode(text[index..next]));
            index = next;
        }
        return result.ToString();
    }
}
