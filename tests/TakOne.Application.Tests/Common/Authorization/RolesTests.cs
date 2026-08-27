using System.Reflection;
using FluentAssertions;
using TakOne.Application.Common.Authorization;
using Xunit;

namespace TakOne.Application.Tests.Common.Authorization;

/// <summary>
/// Unit tests for <see cref="Roles"/> — the standard role-name constants
/// used across the application (and seeded into Identity by the
/// Infrastructure layer at startup).
/// </summary>
public class RolesTests
{
    // ── Per-constant contract ──────────────────────────────────────────

    [Fact]
    public void Admin_WhenRead_EqualsAdmin()
    {
        // Arrange

        // Act
        var value = Roles.Admin;

        // Assert
        value.Should().Be("Admin");
    }

    [Fact]
    public void Manager_WhenRead_EqualsManager()
    {
        // Arrange

        // Act
        var value = Roles.Manager;

        // Assert
        value.Should().Be("Manager");
    }

    [Fact]
    public void Employee_WhenRead_EqualsEmployee()
    {
        // Arrange

        // Act
        var value = Roles.Employee;

        // Assert
        value.Should().Be("Employee");
    }

    [Fact]
    public void ReadOnly_WhenRead_EqualsReadOnly()
    {
        // Arrange

        // Act
        var value = Roles.ReadOnly;

        // Assert
        value.Should().Be("ReadOnly");
    }

    [Fact]
    public void Customer_WhenRead_EqualsCustomer()
    {
        // Arrange

        // Act
        var value = Roles.Customer;

        // Assert
        value.Should().Be("Customer");
    }

    // ── Cross-constant invariants ─────────────────────────────────────

    [Fact]
    public void AllRoles_WhenEnumerated_AreAllNonEmpty()
    {
        // Arrange
        var roles = new[] { Roles.Admin, Roles.Manager, Roles.Employee, Roles.ReadOnly, Roles.Customer };

        // Act / Assert
        foreach (var r in roles)
        {
            r.Should().NotBeNullOrEmpty("role names must be non-empty strings");
        }
    }

    [Fact]
    public void AllRoles_WhenEnumerated_AreAllDistinct()
    {
        // Arrange
        // Each role constant must be a distinct string — the
        // [RequireRoles] attribute looks up role membership by name, so a
        // collision would silently merge two role classes.

        // Act
        var roles = new[] { Roles.Admin, Roles.Manager, Roles.Employee, Roles.ReadOnly, Roles.Customer };

        // Assert
        roles.Distinct().Count().Should().Be(roles.Length,
            "every role constant must be a distinct string");
    }

    [Fact]
    public void AllRoles_WhenEnumerated_MatchTheirExpectedValues()
    {
        // Arrange
        var expected = new[] { "Admin", "Manager", "Employee", "ReadOnly", "Customer" };

        // Act
        var actual = new[] { Roles.Admin, Roles.Manager, Roles.Employee, Roles.ReadOnly, Roles.Customer };

        // Assert
        actual.Should().Equal(expected);
    }

    // ── Class structure ────────────────────────────────────────────────

    [Fact]
    public void Roles_WhenTypeInspected_IsStaticClass()
    {
        // Arrange
        var type = typeof(Roles);

        // Act / Assert
        type.IsSealed.Should().BeTrue("static classes are sealed");
        type.IsAbstract.Should().BeTrue("static classes are abstract");
        type.IsClass.Should().BeTrue();
    }

    // ── Reflective enumeration ─────────────────────────────────────────

    [Fact]
    public void Roles_WhenReflected_HasAtLeastFiveConstStringFields()
    {
        // Arrange
        // Defensive reflection — catches accidental deletion of a role
        // constant. The five current constants are Admin, Manager,
        // Employee, ReadOnly, Customer.

        // Act
        var constFields = typeof(Roles)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            // For `public const string` fields, IsLiteral=true. IsInitOnly
            // is for `readonly` fields, NOT const — do not filter on it.
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToList();

        // Assert
        constFields.Count.Should().BeGreaterThanOrEqualTo(5);
    }
}
