using System.Net;
using System.Text.Json.Nodes;
using Npgsql;

namespace HandyNet.IntegrationTests.Infrastructure;

/// <summary>Shared setup of the POS completion tests (offline, card terminals, display, pricelists, gift cards, loyalty).</summary>
public sealed class PosSupport(HandyNetFixture fixture)
{
    public const string StaffPassword = "Kassierer-Passwort-1";

    public Scenario Scenario { get; } = new(fixture);

    public static Dictionary<string, string> Key(string? key = null) => new() { ["Idempotency-Key"] = key ?? Guid.NewGuid().ToString("N") };

    public static Guid Id(JsonNode node, string property = "id") => Guid.Parse(node[property]!.GetValue<string>());

    public async Task<ApiClient> ShopAsync(string kind = "kassenshop")
    {
        var shop = await Scenario.RegisterAsync(kind, wholesaler: false);
        await Scenario.ApproveAsync(shop, "Pos", "Inventory", "ProductImport");
        return shop;
    }

    public async Task<ApiClient> MemberAsync(ApiClient owner, string role)
    {
        var email = $"pos-{Scenario.UniqueSuffix()}@test.de";
        (await owner.PostAsync("/api/organization/staff", new { email, displayName = role, password = StaffPassword, role })).Expect(HttpStatusCode.OK);
        var client = fixture.CreateClient();
        await client.LoginAsync(email, StaffPassword);
        client.OrganizationId = owner.OrganizationId;
        return client;
    }

    public static async Task<Guid> DefaultWarehouseAsync(ApiClient shop)
    {
        var warehouses = (await shop.GetAsync("/api/inventory/warehouses")).Expect(HttpStatusCode.OK).Json.AsArray();
        if (warehouses.Count > 0)
            return Id(warehouses.First(w => w!["isDefault"]!.GetValue<bool>())!);
        return Id((await shop.PostAsync("/api/inventory/warehouses", new { code = "MAIN", name = "Hauptlager" })).Expect(HttpStatusCode.Created).Json);
    }

    /// <summary>Creates a catalog accessory and books <paramref name="quantity"/> into the default warehouse.</summary>
    public static async Task<Guid> StockProductAsync(ApiClient shop, int quantity, string name = "Ladekabel USB-C")
    {
        var product = (await shop.PostAsync("/api/catalog/products", new
        {
            brand = "Zubehör", name = $"{name} {Scenario.UniqueSuffix()}", category = "Accessory", quality = "Aftermarket", ean = Scenario.NewEan(),
        })).Expect(HttpStatusCode.Created).Json;
        var productId = Id(product);
        var warehouseId = await DefaultWarehouseAsync(shop);
        if (quantity > 0)
            (await shop.PostAsync("/api/inventory/adjustments", new { warehouseId, productId, delta = quantity, reason = "Wareneingang", type = "Receipt" }))
                .Expect(HttpStatusCode.OK);
        return productId;
    }

    public static async Task<Guid> RegisterAsync(ApiClient shop, string name = "Kasse 1") =>
        Id((await shop.PostAsync("/api/pos/registers", new { name })).Expect(HttpStatusCode.Created).Json);

    public static async Task<Guid> RegisterWithSessionAsync(ApiClient shop, string name = "Kasse 1", long openingCashMinor = 100_00)
    {
        var register = await RegisterAsync(shop, name);
        (await shop.PostAsync($"/api/pos/registers/{register}/sessions", new { openingCashMinor })).Expect(HttpStatusCode.Created);
        return register;
    }

    public static Task<ApiResponse> DeleteAsync(ApiClient client, string path) => client.SendAsync(HttpMethod.Delete, path);

    /// <summary>Scalar query as the schema owner (bypasses RLS): for looking at what really is in the database.</summary>
    public async Task<T> OwnerScalarAsync<T>(string sql)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var command = new NpgsqlCommand(sql, owner);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Statement as the schema owner; returns the PostgreSQL error, or null when it went through.</summary>
    public async Task<PostgresException?> OwnerTryAsync(string sql)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var command = new NpgsqlCommand(sql, owner);
        try
        {
            await command.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException ex)
        {
            return ex;
        }
    }

    /// <summary>A connection as the application role with the tenant set, exactly as the API's connections are (RLS applies).</summary>
    public async Task<NpgsqlConnection> ConnectAsAppAsync(Guid? organizationId)
    {
        var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var set = new NpgsqlCommand("SELECT set_config('app.current_org', @org, false)", connection);
        set.Parameters.AddWithValue("org", organizationId?.ToString() ?? string.Empty);
        await set.ExecuteNonQueryAsync();
        return connection;
    }

    public static async Task<long> CountAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>Runs a statement as the application role and returns the SQLSTATE of its failure (null = it succeeded).</summary>
    public static async Task<string?> TryAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        try
        {
            await command.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
    }
}
