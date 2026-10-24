using System;
using ClaudeForVS.Agent;
using ClaudeForVS.ToolWindows;
using Xunit;

namespace ClaudeForVS.Tests;

/// <summary>A subagent or command as the Claude Tasks window shows it.</summary>
public sealed class ToolTaskTests
{
    private DateTime now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    private ToolTask Start(Action? stop = null) => new(ActivityKind.Shell, "dotnet build", stop, () => this.now);

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(4.9, "4s")]
    [InlineData(65, "1m 05s")]
    [InlineData(3720, "1h 02m")]
    [InlineData(-3, "0s")]
    public void FormatsDurations(double seconds, string expected) =>
        Assert.Equal(expected, ToolTask.FormatDuration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void CountsTimeWhileRunningThenStops()
    {
        ToolTask task = this.Start();
        this.now += TimeSpan.FromSeconds(12);
        Assert.True(task.IsRunning);
        Assert.Equal("Running · 12s", task.StatusText);

        task.Finish();
        this.now += TimeSpan.FromMinutes(5);
        Assert.Equal(ToolTaskState.Finished, task.State);
        Assert.Equal("Finished in 12s", task.StatusText);
    }

    [Fact]
    public void StopsOnceAndEndsAsStopped()
    {
        int stops = 0;
        ToolTask task = this.Start(() => stops++);
        Assert.True(task.CanStop);

        task.RequestStop();
        task.RequestStop();
        Assert.Equal(1, stops);
        Assert.False(task.CanStop);
        Assert.Equal("Stopping…", task.StatusText);

        this.now += TimeSpan.FromSeconds(3);
        task.Finish();
        Assert.Equal(ToolTaskState.Stopped, task.State);
        Assert.Equal("Stopped after 3s", task.StatusText);
    }

    [Fact]
    public void CantStopWithoutAStopActionOrOnceFinished()
    {
        Assert.False(this.Start().CanStop);
        ToolTask task = this.Start(() => { });
        task.Finish();
        Assert.False(task.CanStop);
    }

    [Fact]
    public void ResultReplacesLiveOutput()
    {
        ToolTask task = this.Start();
        task.SetOutput("Restoring…\nCompiling…");
        Assert.Equal(2, task.DetailLines.Count);

        task.Finish();
        task.SetResult(ActivityKind.Failed, "Exit code 1\nerror CS0103");
        Assert.True(task.HasResult);
        Assert.Equal(ActivityKind.Failed, task.Activity);
        Assert.Equal("Exit code 1", task.DetailLines[0].Text);
        Assert.StartsWith("Failed after", task.StatusText);
    }

    [Fact]
    public void EmptyResultKeepsLiveOutput()
    {
        ToolTask task = this.Start();
        task.SetOutput("Read Foo.cs");
        task.Finish();
        task.SetResult(ActivityKind.Other, string.Empty);
        Assert.Equal("Read Foo.cs", Assert.Single(task.DetailLines).Text);
    }
}
