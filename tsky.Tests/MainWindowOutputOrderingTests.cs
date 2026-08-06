namespace tsky.Tests;

public sealed class MainWindowOutputOrderingTests
{
    [Fact]
    public void ResolveStoppedRunInsertionIndex_AlreadyAtCompletedBoundary_ReturnsCurrentIndex()
    {
        var runningStates = new[] { true, true, false, false };

        var index = MainWindow.ResolveStoppedRunInsertionIndex(runningStates, currentIndex: 2);

        Assert.Equal(2, index);
    }

    [Fact]
    public void ResolveStoppedRunInsertionIndex_StoppedBetweenRunningItems_MovesBelowRunningSection()
    {
        var runningStates = new[] { true, false, true, false };

        var index = MainWindow.ResolveStoppedRunInsertionIndex(runningStates, currentIndex: 1);

        Assert.Equal(2, index);
    }

    [Fact]
    public void ResolveStoppedRunInsertionIndex_NoRunningItems_RemainsInPlace()
    {
        var runningStates = new[] { false, false, false };

        var index = MainWindow.ResolveStoppedRunInsertionIndex(runningStates, currentIndex: 0);

        Assert.Equal(0, index);
    }

    [Fact]
    public void ResolveStoppedRunInsertionIndex_NewlyCompletedRun_PrecedesOlderCompletedRuns()
    {
        var runningStates = new[] { false, true, false, false };

        var index = MainWindow.ResolveStoppedRunInsertionIndex(runningStates, currentIndex: 0);

        Assert.Equal(1, index);
    }

    [Fact]
    public void ResolveStoppedRunInsertionIndex_InvalidIndex_IsReturnedUnchanged()
    {
        var runningStates = new[] { true, false };

        var negativeIndex = MainWindow.ResolveStoppedRunInsertionIndex(runningStates, currentIndex: -1);
        var tooLargeIndex = MainWindow.ResolveStoppedRunInsertionIndex(runningStates, currentIndex: 4);

        Assert.Equal(-1, negativeIndex);
        Assert.Equal(4, tooLargeIndex);
    }
}
