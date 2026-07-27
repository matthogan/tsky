using System.Globalization;

namespace tsky;

public static class SchedulerTimingService
{
    public const int MinPollIntervalSeconds = 10;
    public const int MaxPollIntervalSeconds = 59;
    public const int DefaultPollIntervalSeconds = 30;
    public const int DefaultTriggerWindowSeconds = 60;

    public static int NormalizePollIntervalSeconds(int? value)
    {
        if (!value.HasValue)
        {
            return DefaultPollIntervalSeconds;
        }

        return Math.Clamp(value.Value, MinPollIntervalSeconds, MaxPollIntervalSeconds);
    }

    public static int NormalizeTriggerWindowSeconds(int? value)
    {
        var candidate = value.GetValueOrDefault(DefaultTriggerWindowSeconds);
        return Math.Max(1, candidate);
    }

    public static string BuildEveryMinutesExpression(int minutes)
    {
        var normalized = Math.Max(1, minutes);
        return $"every:{normalized}m";
    }

    public static bool TryParseEveryMinutesExpression(string? expression, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        const string prefix = "every:";
        var trimmed = expression.Trim();
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !trimmed.EndsWith("m", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var valueSegment = trimmed[prefix.Length..^1];
        if (!int.TryParse(valueSegment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        if (parsed <= 0)
        {
            return false;
        }

        minutes = parsed;
        return true;
    }

    public static bool TryResolveDueOccurrence(
        ScheduledItem item,
        DateTime nowUtc,
        out DateTime occurrenceUtc,
        out string occurrenceToken)
    {
        occurrenceUtc = default;
        occurrenceToken = string.Empty;

        if (!item.IsEnabled || item.IsRunning)
        {
            return false;
        }

        var triggerWindowSeconds = NormalizeTriggerWindowSeconds(item.TriggerWindowSeconds);
        var toleranceWindow = TimeSpan.FromSeconds(triggerWindowSeconds);

        if (item.IsRecurring)
        {
            if (!TryParseEveryMinutesExpression(item.RecurrenceExpression, out var recurrenceMinutes))
            {
                return false;
            }

            var recurrence = TimeSpan.FromMinutes(recurrenceMinutes);
            if (nowUtc < item.TriggerAtUtc)
            {
                return false;
            }

            var elapsed = nowUtc - item.TriggerAtUtc;
            var steps = (long)Math.Floor(elapsed.TotalSeconds / recurrence.TotalSeconds);
            occurrenceUtc = item.TriggerAtUtc.AddSeconds(steps * recurrence.TotalSeconds);

            if (nowUtc < occurrenceUtc)
            {
                return false;
            }

            if (nowUtc - occurrenceUtc > toleranceWindow)
            {
                return false;
            }

            occurrenceToken = BuildOccurrenceToken(item.Id, occurrenceUtc);
            return true;
        }

        occurrenceUtc = item.TriggerAtUtc;
        if (nowUtc < occurrenceUtc)
        {
            return false;
        }

        if (nowUtc - occurrenceUtc > toleranceWindow)
        {
            return false;
        }

        occurrenceToken = BuildOccurrenceToken(item.Id, occurrenceUtc);
        return true;
    }

    public static DateTime? TryResolveNextRunUtc(ScheduledItem item, DateTime fromUtc)
    {
        if (!item.IsRecurring)
        {
            return item.IsEnabled ? item.TriggerAtUtc : null;
        }

        if (!TryParseEveryMinutesExpression(item.RecurrenceExpression, out var recurrenceMinutes))
        {
            return null;
        }

        var recurrence = TimeSpan.FromMinutes(recurrenceMinutes);
        if (fromUtc <= item.TriggerAtUtc)
        {
            return item.TriggerAtUtc;
        }

        var elapsed = fromUtc - item.TriggerAtUtc;
        var steps = (long)Math.Ceiling(elapsed.TotalSeconds / recurrence.TotalSeconds);
        steps = Math.Max(0, steps);
        return item.TriggerAtUtc.AddSeconds(steps * recurrence.TotalSeconds);
    }

    public static string BuildOccurrenceToken(string scheduledItemId, DateTime occurrenceUtc)
    {
        var itemId = string.IsNullOrWhiteSpace(scheduledItemId) ? "unknown" : scheduledItemId.Trim();
        return $"{itemId}:{occurrenceUtc:O}";
    }
}
