namespace FlightLog.Domain.Common;

internal static class Guard
{
    public static string RequiredText(string? value, int maxLength, string field)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainRuleViolationException($"{field}.required", $"{field} is required.");
        }

        if (trimmed.Length > maxLength)
        {
            throw new DomainRuleViolationException($"{field}.too_long", $"{field} must be at most {maxLength} characters.");
        }

        return trimmed;
    }

    public static string? OptionalText(string? value, int maxLength, string field)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > maxLength)
        {
            throw new DomainRuleViolationException($"{field}.too_long", $"{field} must be at most {maxLength} characters.");
        }

        return trimmed;
    }

    public static Guid NotEmpty(Guid value, string field)
    {
        if (value == Guid.Empty)
        {
            throw new DomainRuleViolationException($"{field}.required", $"{field} is required.");
        }

        return value;
    }
}
