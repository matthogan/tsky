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

    [Theory]
    [InlineData(null, SchedulerTimingService.DefaultTriggerWindowSeconds)]
    [InlineData(0, 1)]
    [InlineData(-10, 1)]
    [InlineData(15, 15)]
    public void NormalizeTriggerWindowSeconds_ClampsToAtLeastOneSecond(int? input, int expected)
    {
        var actual = SchedulerTimingService.NormalizeTriggerWindowSeconds(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildEveryMinutesExpression_LowerBoundIsOneMinute()
    {
        var expression = SchedulerTimingService.BuildEveryMinutesExpression(0);

        Assert.Equal("every:1m", expression);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("every:0m")]
    [InlineData("every:-1m")]
    [InlineData("every:Xm")]
    [InlineData("hourly")]
    public void TryParseEveryMinutesExpression_InvalidInput_ReturnsFalse(string? expression)
    {
        var parsed = SchedulerTimingService.TryParseEveryMinutesExpression(expression, out var minutes);

        Assert.False(parsed);
        Assert.Equal(0, minutes);
    }

    [Fact]
    public void TryResolveDueOccurrence_WhenDisabledOrRunning_ReturnsFalse()
    {
        var triggerAtUtc = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
        var disabled = new ScheduledItem
        {
            Id = "disabled",
            TriggerAtUtc = triggerAtUtc,
            TriggerWindowSeconds = 60,
            IsEnabled = false,
            IsRunning = false,
            IsRecurring = false
        };

        var running = new ScheduledItem
        {
            Id = "running",
            TriggerAtUtc = triggerAtUtc,
            TriggerWindowSeconds = 60,
            IsEnabled = true,
            IsRunning = true,
            IsRecurring = false
        };

        var disabledDue = SchedulerTimingService.TryResolveDueOccurrence(disabled, triggerAtUtc, out _, out _);
        var runningDue = SchedulerTimingService.TryResolveDueOccurrence(running, triggerAtUtc, out _, out _);

        Assert.False(disabledDue);
        Assert.False(runningDue);
    }

    [Fact]
    public void TryResolveDueOccurrence_RecurringInvalidOrOutOfWindow_ReturnsFalse()
    {
        var triggerAtUtc = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
        var invalidRecurrence = new ScheduledItem
        {
            Id = "invalid-recur",
            TriggerAtUtc = triggerAtUtc,
            TriggerWindowSeconds = 60,
            IsEnabled = true,
            IsRecurring = true,
            RecurrenceExpression = "bad"
        };

        var outOfWindow = new ScheduledItem
        {
            Id = "old-recur",
            TriggerAtUtc = triggerAtUtc,
            TriggerWindowSeconds = 30,
            IsEnabled = true,
            IsRecurring = true,
            RecurrenceExpression = "every:15m"
        };

        var invalidDue = SchedulerTimingService.TryResolveDueOccurrence(invalidRecurrence, triggerAtUtc.AddMinutes(30), out _, out _);
        var outOfWindowDue = SchedulerTimingService.TryResolveDueOccurrence(outOfWindow, triggerAtUtc.AddMinutes(16), out _, out _);

        Assert.False(invalidDue);
        Assert.False(outOfWindowDue);
    }

    [Fact]
    public void TryResolveNextRunUtc_NonRecurringDisabled_ReturnsNull()
    {
        var item = new ScheduledItem
        {
            TriggerAtUtc = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc),
            IsEnabled = false,
            IsRecurring = false
        };

        var nextRunUtc = SchedulerTimingService.TryResolveNextRunUtc(item, item.TriggerAtUtc.AddMinutes(1));

        Assert.Null(nextRunUtc);
    }

    [Fact]
    public void TryResolveNextRunUtc_RecurringInvalidExpression_ReturnsNull()
    {
        var item = new ScheduledItem
        {
            TriggerAtUtc = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc),
            IsEnabled = true,
            IsRecurring = true,
            RecurrenceExpression = "weekly"
        };

        var nextRunUtc = SchedulerTimingService.TryResolveNextRunUtc(item, item.TriggerAtUtc.AddMinutes(5));

        Assert.Null(nextRunUtc);
    }

    [Fact]
    public void BuildOccurrenceToken_BlankId_UsesUnknownPrefix()
    {
        var when = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);

        var token = SchedulerTimingService.BuildOccurrenceToken("   ", when);

        Assert.StartsWith("unknown:", token, StringComparison.Ordinal);
        Assert.EndsWith(when.ToString("O", System.Globalization.CultureInfo.InvariantCulture), token, StringComparison.Ordinal);
    }
}
