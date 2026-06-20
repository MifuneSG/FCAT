using System.Net;
using System.Text.RegularExpressions;
using FCAT.Models;

namespace FCAT.Services;

/// <summary>
/// Pulls clickable chat-channel links out of a raw fleet MOTD. EVE writes channel links as
/// <c>&lt;url=joinChannel:...&gt;Label&lt;/url&gt;</c>; we keep the whole tag verbatim so re-emitting it
/// reproduces a working link (the channel id is inside the markup and can't be looked up by name).
/// </summary>
public static partial class MotdParser
{
    [GeneratedRegex(@"<url=joinChannel:[^>]*>(?<label>.*?)</url>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex JoinChannelRegex();

    public static List<CapturedChannel> ParseChannelLinks(string motd)
    {
        var result = new List<CapturedChannel>();
        if (string.IsNullOrEmpty(motd)) return result;

        // ESI often returns the MOTD with the markup HTML-escaped (&lt;url=...&gt;); decode so the
        // regex sees real <url=...> tags. Harmless when the text is already unescaped (manual paste).
        motd = WebUtility.HtmlDecode(motd);

        foreach (Match m in JoinChannelRegex().Matches(motd))
        {
            var label = m.Groups["label"].Value.Trim();
            if (label.Length == 0) continue;
            // De-dupe by label; last one wins.
            result.RemoveAll(c => string.Equals(c.Label, label, StringComparison.OrdinalIgnoreCase));
            result.Add(new CapturedChannel { Label = label, Markup = m.Value });
        }
        return result;
    }
}
