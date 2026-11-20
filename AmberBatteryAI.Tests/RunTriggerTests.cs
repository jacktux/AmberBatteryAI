using AmberBatteryAI.Models;
using AmberBatteryAI.Services;
using Xunit;

namespace AmberBatteryAI.Tests;

public class RunTriggerTests
{
    private static OptimizerState NewState() => new(new PromptStorageService(Microsoft.Extensions.Logging.Abstractions.NullLogger<PromptStorageService>.Instance));

    [Fact]
    public void RunsAreScheduledUnlessTriggered()
    {
        var state = NewState();
        Assert.Equal((RunTrigger.Scheduled, (string?)null), state.ConsumeTrigger());

        state.GetDelayToken(CancellationToken.None);
        state.TriggerImmediateRun(RunTrigger.Price, "feed-in is up");
        Assert.Equal((RunTrigger.Price, (string?)"feed-in is up"), state.ConsumeTrigger());
        Assert.Equal((RunTrigger.Scheduled, (string?)null), state.ConsumeTrigger());
    }

    [Fact]
    public void ATriggerDuringARunIsIgnoredAndLeavesNoReason()
    {
        var state = NewState();
        state.SetRunning(true);
        state.TriggerImmediateRun(RunTrigger.Price, "ignored");
        Assert.Equal(RunTrigger.Scheduled, state.ConsumeTrigger().Source);
    }
}