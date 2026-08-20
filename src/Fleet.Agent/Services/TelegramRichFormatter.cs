using Telegram.Bot.Types;

namespace Fleet.Agent.Services;

/// <summary>
/// Converts a permissive Markdown-like subset to Telegram InputRichBlock structures
/// for use with sendRichMessage. Mirrors the token-by-token logic in TelegramFormatter
/// but emits typed blocks instead of HTML strings.
///
/// Recognized subset: **bold**, `inline code`, fenced code blocks, [label](url) links.
/// Anything unrecognized is treated as literal text.
/// </summary>
internal static class TelegramRichFormatter
{
    /// <summary>
    /// Converts <paramref name="text"/> to an array of <see cref="InputRichBlock"/>.
    /// Fenced code fences → <see cref="InputRichBlockPreformatted"/>.
    /// All other content → <see cref="InputRichBlockParagraph"/> with inline
    /// <see cref="RichText"/> elements for bold, code, links, and plain text.
    /// </summary>
    internal static InputRichBlock[] ConvertToRichBlocks(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [new InputRichBlockParagraph { Text = new RichTextText { Text = string.Empty } }];

        var blocks = new List<InputRichBlock>();
        // Inline rich text accumulator for the current paragraph
        var inline = new List<RichText>();

        void FlushParagraph()
        {
            if (inline.Count == 0) return;
            RichText rich = inline.Count == 1
                ? inline[0]
                : new RichTextArray { Array = [.. inline] };
            blocks.Add(new InputRichBlockParagraph { Text = rich });
            inline.Clear();
        }

        int i = 0;
        int len = text.Length;

        while (i < len)
        {
            // Fenced code block: ```lang\n...\n```
            if (i + 2 < len && text[i] == '`' && text[i + 1] == '`' && text[i + 2] == '`')
            {
                int start = i + 3;
                string lang = string.Empty;
                int langEnd = start;
                while (langEnd < len && text[langEnd] != '\n' && text[langEnd] != '`')
                    langEnd++;
                if (langEnd < len && text[langEnd] == '\n')
                {
                    lang = text[start..langEnd].Trim();
                    start = langEnd + 1;
                }
                int closeIdx = IndexOfTripleBacktick(text, start);
                if (closeIdx >= 0)
                {
                    FlushParagraph();
                    var content = text[start..closeIdx];
                    blocks.Add(new InputRichBlockPreformatted
                    {
                        Text = new RichTextText { Text = content },
                        Language = lang.Length > 0 ? lang : null,
                    });
                    i = closeIdx + 3;
                    if (i < len && text[i] == '\n') i++;
                    continue;
                }
                // unmatched ``` — fall through as literal
            }

            // Inline code: `...`
            if (text[i] == '`')
            {
                int closeIdx = text.IndexOf('`', i + 1);
                if (closeIdx > i)
                {
                    inline.Add(new RichTextCode
                    {
                        Text = new RichTextText { Text = text.Substring(i + 1, closeIdx - i - 1) },
                    });
                    i = closeIdx + 1;
                    continue;
                }
            }

            // Bold: **...**  (CommonMark non-space adjacency rule)
            if (i + 1 < len && text[i] == '*' && text[i + 1] == '*')
            {
                int closeIdx = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (closeIdx > i)
                {
                    bool openAdj  = i + 2 < len && !char.IsWhiteSpace(text[i + 2]);
                    bool closeAdj = closeIdx > i + 2 && !char.IsWhiteSpace(text[closeIdx - 1]);
                    if (openAdj && closeAdj)
                    {
                        inline.Add(new RichTextBold
                        {
                            Text = new RichTextText { Text = text.Substring(i + 2, closeIdx - i - 2) },
                        });
                        i = closeIdx + 2;
                        continue;
                    }
                }
            }

            // Link: [label](url)
            if (text[i] == '[')
            {
                int labelClose = text.IndexOf(']', i + 1);
                if (labelClose > i && labelClose + 1 < len && text[labelClose + 1] == '(')
                {
                    int urlClose = text.IndexOf(')', labelClose + 2);
                    if (urlClose > labelClose)
                    {
                        var label = text.Substring(i + 1, labelClose - i - 1);
                        var url   = text.Substring(labelClose + 2, urlClose - labelClose - 2).Trim();
                        if (IsValidUrl(url))
                        {
                            inline.Add(new RichTextUrl
                            {
                                Text = new RichTextText { Text = label },
                                Url  = url,
                            });
                            i = urlClose + 1;
                            continue;
                        }
                    }
                }
            }

            // Literal text — batch until the next special character
            int litStart = i;
            while (i < len)
            {
                if ((i + 2 < len && text[i] == '`' && text[i + 1] == '`' && text[i + 2] == '`') ||
                    text[i] == '`' ||
                    (i + 1 < len && text[i] == '*' && text[i + 1] == '*') ||
                    text[i] == '[')
                    break;
                i++;
            }
            if (i > litStart)
                inline.Add(new RichTextText { Text = text[litStart..i] });
        }

        FlushParagraph();

        if (blocks.Count == 0)
            blocks.Add(new InputRichBlockParagraph { Text = new RichTextText { Text = string.Empty } });

        return [.. blocks];
    }

    private static int IndexOfTripleBacktick(string text, int startFrom)
    {
        int idx = startFrom;
        while (idx < text.Length - 2)
        {
            idx = text.IndexOf('`', idx);
            if (idx < 0) return -1;
            if (idx + 2 < text.Length && text[idx + 1] == '`' && text[idx + 2] == '`')
                return idx;
            idx++;
        }
        return -1;
    }

    private static bool IsValidUrl(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
