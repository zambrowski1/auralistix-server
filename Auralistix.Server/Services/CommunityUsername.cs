using System.Text;

namespace Auralistix.Server.Services;

public static class CommunityUsername
{
    public const int MinLength = 3;
    public const int MaxLength = 24;

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder();
        var pendingUnderscore = false;

        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                if (pendingUnderscore && builder.Length > 0)
                    builder.Append('_');

                builder.Append(character);
                pendingUnderscore = false;

                if (builder.Length >= MaxLength)
                    break;
            }
            else if (builder.Length > 0)
            {
                pendingUnderscore = true;
            }
        }

        while (builder.Length > 0 && builder[^1] == '_')
            builder.Length--;

        return builder.ToString();
    }

    public static bool IsValid(string? value)
    {
        var normalized = Normalize(value);
        return normalized.Length is >= MinLength and <= MaxLength;
    }

    public static string BuildSeed(string? preferredUsername, string? displayName, string email)
    {
        var preferred = Normalize(preferredUsername);
        if (preferred.Length >= MinLength)
            return preferred;

        var display = Normalize(displayName);
        if (display.Length >= MinLength)
            return display;

        var emailLocalPart = email.Split('@', 2)[0];
        var emailSeed = Normalize(emailLocalPart);
        if (emailSeed.Length >= MinLength)
            return emailSeed;

        if (!string.IsNullOrWhiteSpace(preferred))
            return PadSeed(preferred);

        if (!string.IsNullOrWhiteSpace(display))
            return PadSeed(display);

        if (!string.IsNullOrWhiteSpace(emailSeed))
            return PadSeed(emailSeed);

        return "user";
    }

    public static string WithSuffix(string seed, int index)
    {
        seed = Normalize(seed);
        if (string.IsNullOrWhiteSpace(seed))
            seed = "user";

        if (index <= 1)
            return seed.Length <= MaxLength ? seed : seed[..MaxLength];

        var suffix = $"_{index}";
        var prefixLength = Math.Max(MinLength, MaxLength - suffix.Length);
        var prefix = seed.Length <= prefixLength ? seed : seed[..prefixLength];
        return $"{prefix}{suffix}";
    }

    public static string DescribeRules() => "Use 3-24 lowercase Latin letters, numbers or underscores.";

    private static string PadSeed(string seed)
    {
        if (seed.Length >= MinLength)
            return seed;

        return seed.PadRight(MinLength, 'x');
    }
}
