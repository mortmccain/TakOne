using System.Reflection;
using System.Text;
using FluentAssertions;
using TakOne.Application.Notifications.Errors;
using Xunit;

namespace TakOne.Application.Tests.Notifications;

/// <summary>
/// Unit tests for <see cref="NotificationErrors"/> — the culture-neutral
/// stable-code catalog for notification-related failures (auth-required,
/// not-found, broadcast-specific errors). Each Format* method returns a
/// short, stable, no-parameter Pascal-case string the UI localizes via
/// IStringLocalizer.
/// </summary>
public class NotificationErrorsTests
{
    // ── Format* method return-value contracts ──────────────────────────

    [Fact]
    public void FormatAuthRequired_WhenCalled_ReturnsNotificationAuthRequired()
    {
        // Arrange

        // Act
        var result = NotificationErrors.FormatAuthRequired();

        // Assert
        result.Should().Be("NotificationAuthRequired");
    }

    [Fact]
    public void FormatNotFound_WhenCalled_ReturnsNotificationNotFound()
    {
        // Arrange

        // Act
        var result = NotificationErrors.FormatNotFound();

        // Assert
        result.Should().Be("NotificationNotFound");
    }

    [Fact]
    public void FormatBroadcastAuthRequired_WhenCalled_ReturnsBroadcastAuthRequired()
    {
        // Arrange

        // Act
        var result = NotificationErrors.FormatBroadcastAuthRequired();

        // Assert
        result.Should().Be("BroadcastAuthRequired");
    }

    [Fact]
    public void FormatBroadcastGroupNotFound_WhenCalled_ReturnsBroadcastGroupNotFound()
    {
        // Arrange

        // Act
        var result = NotificationErrors.FormatBroadcastGroupNotFound();

        // Assert
        result.Should().Be("BroadcastGroupNotFound");
    }

    [Fact]
    public void FormatBroadcastUserNotFound_WhenCalled_ReturnsBroadcastUserNotFound()
    {
        // Arrange

        // Act
        var result = NotificationErrors.FormatBroadcastUserNotFound();

        // Assert
        result.Should().Be("BroadcastUserNotFound");
    }

    [Fact]
    public void FormatBroadcastUserInactive_WhenCalled_ReturnsBroadcastUserInactive()
    {
        // Arrange

        // Act
        var result = NotificationErrors.FormatBroadcastUserInactive();

        // Assert
        result.Should().Be("BroadcastUserInactive");
    }

    // ── Idempotency ─────────────────────────────────────────────────────

    [Fact]
    public void FormatAuthRequired_WhenCalledTwice_ReturnsSameValue()
    {
        // Arrange
        // Format() methods are pure — no randomness, no clock, no env.

        // Act
        var first = NotificationErrors.FormatAuthRequired();
        var second = NotificationErrors.FormatAuthRequired();

        // Assert
        first.Should().Be(second);
    }

    // ── Class structure (reflection-based) ─────────────────────────────

    [Fact]
    public void NotificationErrors_WhenTypeInspected_IsStaticClass()
    {
        // Arrange
        var type = typeof(NotificationErrors);

        // Act / Assert
        type.IsSealed.Should().BeTrue("static classes are sealed");
        type.IsAbstract.Should().BeTrue("static classes are abstract");
        type.IsClass.Should().BeTrue();
    }

    [Fact]
    public void FormatMethods_WhenInspected_AreAllPublicStatic()
    {
        // Arrange
        var type = typeof(NotificationErrors);

        // Act
        var formatMethods = type
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name.StartsWith("Format", StringComparison.Ordinal))
            .ToList();

        // Assert
        formatMethods.Should().NotBeEmpty();
        foreach (var method in formatMethods)
        {
            method.IsPublic.Should().BeTrue($"{method.Name} must be public");
            method.IsStatic.Should().BeTrue($"{method.Name} must be static");
        }
    }

    [Fact]
    public void FormatMethods_WhenInspected_TakeNoParameters()
    {
        // Arrange
        // The SUT doc says "Today's notification errors don't need params."
        // Verify by reflection — every Format* method must have 0
        // parameters. A future change adding params would silently break
        // the UI's localization key lookup.

        // Act
        var formatMethods = typeof(NotificationErrors)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name.StartsWith("Format", StringComparison.Ordinal))
            .ToList();

        // Assert
        foreach (var method in formatMethods)
        {
            method.GetParameters().Should().BeEmpty(
                $"{method.Name} must take 0 parameters");
        }
    }

    [Fact]
    public void FormatMethods_WhenInspected_ReturnString()
    {
        // Arrange
        // All Format* methods return a string code — verify by reflection.

        // Act
        var formatMethods = typeof(NotificationErrors)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name.StartsWith("Format", StringComparison.Ordinal))
            .ToList();

        // Assert
        foreach (var method in formatMethods)
        {
            method.ReturnType.Should().Be(typeof(string),
                $"{method.Name} must return string");
        }
    }

    [Fact]
    public void AllCodes_WhenEnumerated_AreAllDistinct()
    {
        // Arrange
        // Every Format* method returns a unique code — no two methods may
        // return the same string (the UI's IStringLocalizer key lookup
        // would otherwise be ambiguous).

        // Act
        var codes = new[]
        {
            NotificationErrors.FormatAuthRequired(),
            NotificationErrors.FormatNotFound(),
            NotificationErrors.FormatBroadcastAuthRequired(),
            NotificationErrors.FormatBroadcastGroupNotFound(),
            NotificationErrors.FormatBroadcastUserNotFound(),
            NotificationErrors.FormatBroadcastUserInactive(),
        };

        // Assert
        codes.Distinct().Count().Should().Be(codes.Length,
            "every Format* method must return a distinct code");
    }

    [Fact]
    public void AllCodes_WhenEnumerated_AreAllNonEmpty()
    {
        // Arrange
        var codes = new[]
        {
            NotificationErrors.FormatAuthRequired(),
            NotificationErrors.FormatNotFound(),
            NotificationErrors.FormatBroadcastAuthRequired(),
            NotificationErrors.FormatBroadcastGroupNotFound(),
            NotificationErrors.FormatBroadcastUserNotFound(),
            NotificationErrors.FormatBroadcastUserInactive(),
        };

        // Act / Assert
        foreach (var code in codes)
        {
            code.Should().NotBeNullOrEmpty("every code must be a non-empty string");
        }
    }
}
