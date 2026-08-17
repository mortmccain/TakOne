using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Common.Entities;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Settings.Commands.SetSystemLimitMode;

public sealed class SetSystemLimitModeCommandHandler
{
    public static async Task<Result> HandleAsync(
        SetSystemLimitModeCommand command,
        ICurrentUserService currentUser,
        ISystemSettingsRepository systemSettingsRepository,
        ISystemSettingsService systemSettingsService,
        ILogger<SetSystemLimitModeCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("SetSystemLimitMode: unauthenticated call rejected.");
            return Result.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the singleton (tracked — we'll mutate it). The repo's
        //    GetOrCreateAsync lazily creates the singleton row with the
        //    default mode (CountOnly) on first read.
        // ------------------------------------------------------------------
        var settings = await systemSettingsRepository.GetOrCreateAsync(cancellationToken);

        var previousMode = settings.LimitMode;

        // ------------------------------------------------------------------
        // 2. Mutate the aggregate. UpdateLimitMode enforces the domain
        //    invariant (enum value != 0) and bumps UpdatedAt.
        // ------------------------------------------------------------------
        settings.UpdateLimitMode(command.NewMode);

        // ------------------------------------------------------------------
        // 3. Persist. The repo's UpdateAsync calls SaveChangesAsync
        //    internally (NOT deferred to UoW) so the DB write commits
        //    BEFORE we invalidate the cache. This prevents a race where a
        //    concurrent reader re-populates the cache with the OLD value
        //    after invalidation but before SaveChanges.
        // ------------------------------------------------------------------
        await systemSettingsRepository.UpdateAsync(settings, cancellationToken);

        // ------------------------------------------------------------------
        // 4. Invalidate the cache. The next read via
        //    ISystemSettingsService.GetLimitModeAsync will re-load from DB
        //    and see the new mode.
        // ------------------------------------------------------------------
        await systemSettingsService.InvalidateCacheAsync(cancellationToken);

        logger.LogInformation(
            "SetSystemLimitMode: limit mode changed from {PreviousMode} to {NewMode} by user {UserId}.",
            previousMode, command.NewMode, currentUser.UserId);

        return Result.Success();
    }
}