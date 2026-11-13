using WSLProxy.Relay.Routing;

namespace WSLProxy.UI.Settings;

/// <summary>The result of reading text the user typed or pasted into the bypass list.</summary>
/// <param name="Added">New entries, normalised, in the order they were given. Entries already in the list are left out.</param>
/// <param name="Rejected">The parts that aren't valid entries, as typed, so the user can fix them.</param>
/// <param name="Problems">Why each rejected part was rejected.</param>
public sealed record BypassInput(IReadOnlyList<string> Added, IReadOnlyList<string> Rejected, IReadOnlyList<string> Problems);

/// <summary>Reads bypass list entries the user typed, including a whole pasted <c>no_proxy</c> value.</summary>
public static class BypassEntries
{
    private static readonly char[] Separators = [',', ';', ' ', '\t', '\r', '\n'];

    /// <summary>
    /// Splits <paramref name="text"/> on commas, semicolons and white space, and checks each part with
    /// <see cref="NoProxyRule.TryParse"/>. A part that normalises to an entry already in <paramref name="existing"/>, or
    /// earlier in the text, isn't added twice.
    /// </summary>
    public static BypassInput Read(string text, IEnumerable<string> existing)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(existing);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in existing)
        {
            seen.Add(NoProxyRule.TryParse(entry, out var rule, out _) ? rule.Normalised : entry);
        }

        var added = new List<string>();
        var rejected = new List<string>();
        var problems = new List<string>();
        foreach (var part in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!NoProxyRule.TryParse(part, out var rule, out var error))
            {
                rejected.Add(part);
                problems.Add(part + ": " + error);
            }
            else if (seen.Add(rule.Normalised))
            {
                added.Add(rule.Normalised);
            }
        }

        return new BypassInput(added, rejected, problems);
    }
}
