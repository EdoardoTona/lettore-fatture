using System.Text;
using System.Text.RegularExpressions;
using MimeKit.Text;

namespace Fatture.Core;

// Parse HTML with MimeKit's tokenizer; never execute markup or fetch external resources.
internal static partial class EmailHtml
{
    public static string ToText(string html)
    {
        if (html.Length > 1_000_000) throw new InvalidDataException("Il corpo HTML supera il limite di un milione di caratteri.");
        using var reader = new StringReader(html);
        var tokenizer = new HtmlTokenizer(reader) { DecodeCharacterReferences = true };
        var output = new StringBuilder();
        string? hidden = null;
        while (tokenizer.ReadNextToken(out var token))
        {
            if (token is HtmlTagToken tag)
            {
                var name = tag.Name.ToLowerInvariant();
                if (hidden != null) { if (tag.IsEndTag && name == hidden) hidden = null; continue; }
                if (name is "script" or "style" or "head") { if (!tag.IsEndTag) hidden = name; continue; }
                if (name is "br" or "p" or "div" or "tr" or "h1" or "h2" or "h3" or "h4" or "li" or "blockquote" or "hr") output.Append('\n');
                if (name is "td" or "th") output.Append("  ");
            }
            else if (hidden == null && token is HtmlDataToken data) output.Append(data.Data);
        }
        var text = HorizontalSpace().Replace(output.ToString(), " ");
        return BlankLines().Replace(text, "\n\n").Trim();
    }
    [GeneratedRegex(@"[^\S\n]+", RegexOptions.NonBacktracking)] private static partial Regex HorizontalSpace();
    [GeneratedRegex(@"\n[ \n]*\n", RegexOptions.NonBacktracking)] private static partial Regex BlankLines();
}
