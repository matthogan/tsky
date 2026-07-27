namespace tsky.Tests;

public sealed class SchedulerTimingServiceTests
{
    [Theory]
    [InlineData(null, SchedulerTimingService.DefaultPollIntervalSeconds)]
    [InlineData(1, SchedulerTimingService.MinPollIntervalSeconds)]
    [InlineData(10, 10)]
    [InlineData(59, 59)]
    [InlineData(120, SchedulerTimingService.MaxPollIntervalSeconds)]
    public void NormalizePollIntervalSeconds_ClampsToSupportedRange(int? input, int expected)
    {
        var actual = SchedulerTimingService.NormalizePollIntervalSeconds(input);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("every:1m", 1)]
    [InlineData("every:15m", 15)]
    [InlineData("EVERY:60M", 60)]
    public void TryParseEveryMinutesExpression_ParsesValidInput(string expression, int expectedMinutes)
    {
        var parsed = SchedulerTimingService.TryParseEveryMinutesExpression(expression, out var minutes);

        Assert.True(parsed);
        Assert.Equal(expectedMinutes, minutes);
    }

    [Fact]
    public void TryResolveDueOccurrence_OneShotWithinWindow_ReturnsDueOccurrence()
    {
        var triggerAtUtc = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
        var item = new ScheduledItem
        {
            Id = "item-a",
            TriggerAtUtc = triggerAtUtc,
            TriggerWindowSeconds = 60,
            IsEnabled = true,
            IsRecurring = false
        };

        var isDue = SchedulerTimingService.TryResolveDueOccurrence(
            item,
            triggerAtUtc.AddSeconds(30),
            out var occurrenceUtc,
            out var token);

        Assert.True(isDue);
        Assert.Equal(triggerAtUtc, occurrenceUtc);
        Assert.Equal(SchedulerTimingService.BuildOccurrenceToken(item.Id, triggerAtUtc), token);
    }

    [Fact]
    public void TryResolveDueOccurrence_OneShotOutsideWindow_IsNotDue()
    {
        var triggerAtUtc = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
        var item = new ScheduledItem
        {
            Id = "item-b",
            TriggerAtUtc = triggerAtUtc,
            TriggerWindowSeconds = 60,
            IsEnabled = true,
            IsRecurring = false
        };

        var isDue = SchedulerTimingService.TryResolveDueOccurrence(
            item,
            triggerAtUtc.AddSeconds(61),
            out _,
            out _);

        Assert.False(isDue);
    }

    [Fact]
    public void TryResolveDueOccurrence_RecurringWithinWindow_UsesLatestOccurrence()
    {
        var triggerAtUtc = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
        var item = new ScheduledItem
        {
            Id = "item-c",
            TriggerAtUtc = triggerAtUtc,
            TriggerWindowSeconds = 60,
            IsEnabled = true,
            IsRecurring = true,
            RecurrenceExpression = "every:15m"
        };

        var nowUtc = triggerAtUtc.AddMinutes(45).AddSeconds(20);

        var isDue = SchedulerTimingService.TryResolveDueOccurrence(
            item,
            nowUtc,
            out var occurrenceUtc,
            out _);

        Assert.True(isDue);
        Assert.Equal(triggerAtUtc.AddMinutes(45), occurrenceUtc);
    }

    [Fact]
    public void TryResolveNextRunUtc_Recurring_ReturnsNextFutureBoundary()
    {
        var triggerAtUtc = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
        var item = new ScheduledItem
        {
            TriggerAtUtc = triggerAtUtc,
            IsEnabled = true,
            IsRecurring = true,
            RecurrenceExpression = "every:30m"
        };

        var nextRunUtc = SchedulerTimingService.TryResolveNextRunUtc(item, triggerAtUtc.AddMinutes(46));

        Assert.Equal(triggerAtUtc.AddMinutes(60), nextRunUtc);
    }
}
