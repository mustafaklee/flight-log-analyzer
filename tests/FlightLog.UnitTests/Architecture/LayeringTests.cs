using System.Reflection;

namespace FlightLog.UnitTests.Architecture;

/// <summary>Guards the Clean Architecture dependency direction described in docs/architecture.md.</summary>
public sealed class LayeringTests
{
    private static readonly Assembly Domain = typeof(FlightLog.Domain.Teams.Team).Assembly;
    private static readonly Assembly Application = typeof(FlightLog.Application.DependencyInjection).Assembly;
    private static readonly Assembly Infrastructure = typeof(FlightLog.Infrastructure.DependencyInjection).Assembly;

    [Fact]
    public void Domain_has_no_dependencies_beyond_the_base_library()
    {
        var references = Domain.GetReferencedAssemblies().Select(a => a.Name).ToList();

        references.Should().OnlyContain(name => name == "System.Runtime" || name!.StartsWith("System.", StringComparison.Ordinal),
            "the domain model must not depend on frameworks or other layers");
    }

    [Fact]
    public void Application_depends_only_on_domain_and_approved_libraries()
    {
        var references = Application.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        references.Should().NotContain(n => n.StartsWith("FlightLog.Infrastructure", StringComparison.Ordinal));
        references.Should().NotContain(n => n.StartsWith("FlightLog.Api", StringComparison.Ordinal));
        references.Should().NotContain(n => n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        references.Should().NotContain(n => n.StartsWith("Npgsql", StringComparison.Ordinal));
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_the_api()
    {
        Infrastructure.GetReferencedAssemblies().Select(a => a.Name!)
            .Should().NotContain(n => n.StartsWith("FlightLog.Api", StringComparison.Ordinal));
    }

    [Fact]
    public void Domain_entities_do_not_expose_public_setters()
    {
        var violations = Domain.GetExportedTypes()
            .Where(t => t.IsClass && !typeof(Exception).IsAssignableFrom(t))
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.SetMethod?.IsPublic == true && !IsInitOnly(p))
                .Select(p => $"{t.Name}.{p.Name}"))
            .ToList();

        violations.Should().BeEmpty("domain state changes go through methods that enforce invariants");
    }

    // Init-only setters belong to immutable value records (for example AircraftDetails), which cannot change after creation.
    private static bool IsInitOnly(PropertyInfo property) =>
        property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit));
}
