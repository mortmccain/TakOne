using FluentAssertions;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Settings.Commands.TriggerBackupNow;
using TakOne.SharedKernel.Common;
using Xunit;

namespace TakOne.Application.Tests.Settings.Commands.TriggerBackupNow;

/// <summary>
/// Unit tests for <see cref="TriggerBackupNowCommandHandler"/> — the
/// per-reason mapping from <see cref="BackupTriggerOutcome"/> to the
/// user-facing rejection message (QA-5 handover item). The coordinator is
/// substituted; only the handler's mapping logic is under test.
///
/// COVERAGE APPROACH:
///   • Accepted → Result success (no message)
///   • RejectedInFlight / RejectedPending / RejectedDisabled → each its
///     own DISTINCT failure message (the old contract returned one
///     generic "in progress or disabled" string for all three causes)
///   • Distinctness matters: the Settings card shows result.Error in a
///     toast, and an admin who can tell "wait for the current run" from
///     "the schedule is off in config" acts on the right problem.
/// </summary>
public class TriggerBackupNowCommandHandlerTests
{
    private static TriggerBackupNowCommand Command() => new();

    private static IBackupCoordinator CoordinatorReturning(BackupTriggerOutcome outcome)
    {
        var coordinator = Substitute.For<IBackupCoordinator>();
        coordinator.TryTriggerNow().Returns(outcome);
        return coordinator;
    }

    [Fact]
    public async Task HandleAsync_WhenAccepted_ReturnsSuccess()
    {
        var coordinator = CoordinatorReturning(BackupTriggerOutcome.Accepted);

        var result = await TriggerBackupNowCommandHandler.HandleAsync(
            Command(), coordinator, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(BackupTriggerOutcome.RejectedInFlight, "in progress")]
    [InlineData(BackupTriggerOutcome.RejectedPending, "already been requested")]
    [InlineData(BackupTriggerOutcome.RejectedDisabled, "disabled")]
    public async Task HandleAsync_WhenRejected_ReturnsPerReasonMessage(
        BackupTriggerOutcome outcome,
        string expectedFragment)
    {
        var coordinator = CoordinatorReturning(outcome);

        var result = await TriggerBackupNowCommandHandler.HandleAsync(
            Command(), coordinator, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain(expectedFragment);
    }

    [Fact]
    public async Task HandleAsync_RejectionMessages_AreAllDistinct()
    {
        var outcomes = new[]
        {
            BackupTriggerOutcome.RejectedInFlight,
            BackupTriggerOutcome.RejectedPending,
            BackupTriggerOutcome.RejectedDisabled
        };

        var messages = new List<string>();
        foreach (var outcome in outcomes)
        {
            var result = await TriggerBackupNowCommandHandler.HandleAsync(
                Command(), CoordinatorReturning(outcome), CancellationToken.None);

            messages.Add(result.Error);
        }

        // Three causes → three DIFFERENT messages (no generic collapse).
        messages.Should().OnlyHaveUniqueItems();
    }
}
