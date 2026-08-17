using TakOne.Domain.Common.Enums;

namespace TakOne.Application.Settings.DTOs;

/// <summary>
/// Read-side DTO for the system-wide settings singleton.
///
/// Currently exposes just the <see cref="LimitMode"/> (CountOnly /
/// SalaryOnly / Both), but the schema is designed to grow — future
/// system-wide settings (e.g. default currency, locale, maintenance mode)
/// should be added to the <c>SystemSettings</c> domain entity and
/// projected here.
///
/// Used by the Settings page (read) and the SetSystemLimitMode command
/// (write).
/// </summary>
public sealed class SystemSettingsDto
{
    /// <summary>
    /// The system-wide limit mode. Controls whether per-product count
    /// limits and/or monthly salary budgets are enforced.
    /// </summary>
    public LimitMode LimitMode { get; init; }

    /// <summary>
    /// When the limit mode was last changed (UTC). For audit display.
    /// </summary>
    public DateTime UpdatedAtUtc { get; init; }
}