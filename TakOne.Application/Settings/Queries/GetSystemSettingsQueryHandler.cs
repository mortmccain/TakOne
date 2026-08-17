using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Settings.DTOs;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Settings.Queries.GetSystemSettings;

public sealed class GetSystemSettingsQueryHandler
{
    public static async Task<Result<SystemSettingsDto>> HandleAsync(
        GetSystemSettingsQuery query,
        ISystemSettingsService systemSettingsService,
        ILogger<GetSystemSettingsQueryHandler> logger,
        CancellationToken cancellationToken)
    {
        // The cached service returns a defensive snapshot — safe to read
        // the LimitMode + UpdatedAt without tracking concerns.
        var settings = await systemSettingsService.GetAsync(cancellationToken);

        var dto = new SystemSettingsDto
        {
            LimitMode = settings.LimitMode,
            UpdatedAtUtc = settings.UpdatedAt
        };

        return Result<SystemSettingsDto>.Success(dto);
    }
}
