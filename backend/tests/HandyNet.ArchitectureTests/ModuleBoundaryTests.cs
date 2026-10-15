using System.Reflection;
using NetArchTest.Rules;

namespace HandyNet.ArchitectureTests;

/// <summary>Enforces the module dependency map in docs/architecture.md.</summary>
public class ModuleBoundaryTests
{
    private static Assembly Module(string name) => Assembly.Load($"HandyNet.Modules.{name}");

    public static TheoryData<string, string> ForbiddenDependencies => new()
    {
        // Lower layers never depend on higher ones.
        { "Identity", "HandyNet.Modules.Tenancy" },
        { "Catalog", "HandyNet.Modules.Inventory" },
        { "Catalog", "HandyNet.Modules.Marketplace" },
        { "Inventory", "HandyNet.Modules.Marketplace" },
        { "Inventory", "HandyNet.Modules.Ordering" },
        { "Marketplace", "HandyNet.Modules.Ordering" },
        { "Marketplace", "HandyNet.Modules.Imports" },
        { "Imports", "HandyNet.Modules.Ordering" },
        { "Ordering", "HandyNet.Modules.Imports" },
        // Notifications only consumes events; nothing may depend on it.
        { "Ordering", "HandyNet.Modules.Notifications" },
        { "Tenancy", "HandyNet.Modules.Notifications" },
        { "Identity", "HandyNet.Modules.Notifications" },
        { "Marketplace", "HandyNet.Modules.Notifications" },
        // Shipping sits above Ordering; Ordering talks to it only through events.
        { "Ordering", "HandyNet.Modules.Shipping" },
        { "Inventory", "HandyNet.Modules.Shipping" },
        { "Catalog", "HandyNet.Modules.Shipping" },
        // Ledger is a generic bookkeeping core; it knows nothing about payments or orders.
        { "Ledger", "HandyNet.Modules.Payments" },
        { "Ledger", "HandyNet.Modules.Ordering" },
        { "Ordering", "HandyNet.Modules.Payments" },
        { "Ordering", "HandyNet.Modules.Ledger" },
        { "Ordering", "HandyNet.Modules.Disputes" },
        { "Payments", "HandyNet.Modules.Disputes" },
        // Ankauf sits on top (uses Catalog, Inventory and Tenancy contracts); nothing below depends on it.
        { "Catalog", "HandyNet.Modules.Ankauf" },
        { "Ordering", "HandyNet.Modules.Ankauf" },
        { "Tenancy", "HandyNet.Modules.Ankauf" },
        { "Inventory", "HandyNet.Modules.Ankauf" },
        // POS sits on top of Inventory, Catalog and Tenancy; nothing depends on it, and it does not use the B2B money flow.
        { "Inventory", "HandyNet.Modules.Pos" },
        { "Catalog", "HandyNet.Modules.Pos" },
        { "Tenancy", "HandyNet.Modules.Pos" },
        { "Ordering", "HandyNet.Modules.Pos" },
        { "Ankauf", "HandyNet.Modules.Pos" },
        { "Payments", "HandyNet.Modules.Pos" },
        { "Pos", "HandyNet.Modules.Ordering" },
        { "Pos", "HandyNet.Modules.Payments" },
        { "Pos", "HandyNet.Modules.Ledger" },
        { "Pos", "HandyNet.Modules.Ankauf" },
        { "Pos", "HandyNet.Modules.Marketplace" },
        // Invoicing sits on top of Payments and Tenancy contracts; nothing depends on it.
        { "Payments", "HandyNet.Modules.Invoicing" },
        { "Tenancy", "HandyNet.Modules.Invoicing" },
        { "Ordering", "HandyNet.Modules.Invoicing" },
        { "Ledger", "HandyNet.Modules.Invoicing" },
        { "Pos", "HandyNet.Modules.Invoicing" },
        { "Ankauf", "HandyNet.Modules.Invoicing" },
        { "Disputes", "HandyNet.Modules.Invoicing" },
        { "Shipping", "HandyNet.Modules.Invoicing" },
        { "Invoicing", "HandyNet.Modules.Ordering" },
        { "Invoicing", "HandyNet.Modules.Ledger" },
        { "Invoicing", "HandyNet.Modules.Pos" },
        { "Invoicing", "HandyNet.Modules.Ankauf" },
    };

    [Fact]
    public void Invoicing_uses_only_the_contracts_of_payments_and_tenancy()
    {
        var result = Types.InAssembly(Module("Invoicing"))
            .ShouldNot().HaveDependencyOnAny(
                "HandyNet.Modules.Payments.Domain", "HandyNet.Modules.Payments.Application", "HandyNet.Modules.Payments.Infrastructure",
                "HandyNet.Modules.Tenancy.Domain", "HandyNet.Modules.Tenancy.Application", "HandyNet.Modules.Tenancy.Infrastructure")
            .GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Theory]
    [MemberData(nameof(ForbiddenDependencies))]
    public void Module_does_not_depend_on(string module, string forbidden)
    {
        var result = Types.InAssembly(Module(module)).ShouldNot().HaveDependencyOn(forbidden).GetResult();
        Assert.True(result.IsSuccessful, $"{module} → {forbidden}: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Theory]
    [InlineData("Identity")]
    [InlineData("Tenancy")]
    [InlineData("Catalog")]
    [InlineData("Inventory")]
    [InlineData("Marketplace")]
    [InlineData("Imports")]
    [InlineData("Ordering")]
    [InlineData("Notifications")]
    [InlineData("Shipping")]
    [InlineData("Ledger")]
    [InlineData("Payments")]
    [InlineData("Disputes")]
    [InlineData("Ankauf")]
    [InlineData("Pos")]
    [InlineData("Invoicing")]
    public void Domain_is_free_of_infrastructure_and_web(string module)
    {
        var result = Types.InAssembly(Module(module))
            .That().ResideInNamespace($"HandyNet.Modules.{module}.Domain")
            .ShouldNot().HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql", "HandyNet.Persistence")
            .GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Theory]
    [InlineData("Catalog", "Inventory")]
    [InlineData("Inventory", "Marketplace")]
    [InlineData("Marketplace", "Imports")]
    [InlineData("Marketplace", "Ordering")]
    [InlineData("Tenancy", "Ordering")]
    [InlineData("Ordering", "Shipping")]
    [InlineData("Catalog", "Shipping")]
    [InlineData("Ledger", "Payments")]
    [InlineData("Ordering", "Payments")]
    [InlineData("Ordering", "Disputes")]
    [InlineData("Inventory", "Ankauf")]
    [InlineData("Catalog", "Ankauf")]
    [InlineData("Tenancy", "Ankauf")]
    [InlineData("Inventory", "Pos")]
    [InlineData("Catalog", "Pos")]
    [InlineData("Tenancy", "Pos")]
    [InlineData("Payments", "Invoicing")]
    [InlineData("Tenancy", "Invoicing")]
    public void Other_modules_use_only_contracts_and_domain_types(string provider, string consumer)
    {
        var result = Types.InAssembly(Module(consumer))
            .ShouldNot().HaveDependencyOnAny($"HandyNet.Modules.{provider}.Application", $"HandyNet.Modules.{provider}.Infrastructure")
            .GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Money_is_never_floating_point()
    {
        var offending = Types.InAssemblies(new[] { "Catalog", "Inventory", "Marketplace", "Imports", "Ordering", "Notifications", "Shipping", "Ledger", "Payments", "Disputes", "Ankauf", "Pos", "Invoicing" }.Select(Module))
            .GetTypes()
            .SelectMany(t => t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(p => (p.Name.Contains("Price") || p.Name.Contains("Total") || p.Name.EndsWith("Minor"))
                        && (p.PropertyType == typeof(double) || p.PropertyType == typeof(float)
                            || p.PropertyType == typeof(double?) || p.PropertyType == typeof(float?)))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")
            .ToList();
        Assert.Empty(offending);
    }
}
