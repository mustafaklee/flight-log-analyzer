namespace FlightLog.Domain.Common;

/// <summary>Normalizes free-form labels: trimmed, de-duplicated (case-insensitive), bounded in count and length.</summary>
public static class Tags
{
    public const int MaxCount = 20;
    public const int MaxLength = 40;

    public static List<string> Normalize(IEnumerable<string>? tags)
    {
        var result = new List<string>();
        foreach (var raw in tags ?? [])
        {
            var tag = raw?.Trim();
            if (string.IsNullOrEmpty(tag) || result.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (tag.Length > MaxLength)
            {
                throw new DomainRuleViolationException("tags.too_long", $"Tags must be at most {MaxLength} characters.");
            }

            result.Add(tag);
        }

        if (result.Count > MaxCount)
        {
            throw new DomainRuleViolationException("tags.too_many", $"At most {MaxCount} tags are allowed.");
        }

        return result;
    }
}
