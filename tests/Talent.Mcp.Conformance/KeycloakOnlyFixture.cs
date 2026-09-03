namespace Talent.Mcp.Conformance;

using Testcontainers.Keycloak;
using Xunit;

/// <summary>
/// A real Keycloak, importing the exact realm <c>deploy/compose.yaml</c> does — with no MCP server
/// alongside it. What this level asserts is the realm's own OAuth metadata and PKCE enforcement, which
/// exist independently of the tool surface; starting <c>Talent.Mcp.Server</c> too would only add a
/// Postgres dependency and a slower fixture for tests that never call it.
/// </summary>
public sealed class KeycloakOnlyFixture : IAsyncLifetime
{
    /// <summary>Pinned to the same image tag as <c>deploy/compose.yaml</c>.</summary>
    private readonly KeycloakContainer container = new KeycloakBuilder("quay.io/keycloak/keycloak:26.7.2")
        .WithRealm(RealmConfigurationFilePath)
        .Build();

    /// <summary>The realm's issuer, e.g. <c>http://127.0.0.1:32812/realms/talent</c>.</summary>
    public Uri Authority { get; private set; } = null!;

    private static string RealmConfigurationFilePath { get; } = ResolveRealmConfigurationFilePath();

    private static string ResolveRealmConfigurationFilePath(
        [System.Runtime.CompilerServices.CallerFilePath] string thisFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFilePath)!, "..", "..", "deploy", "keycloak", "realm.json"));

    /// <summary>Starts the container.</summary>
    public async Task InitializeAsync()
    {
        await this.container.StartAsync().ConfigureAwait(false);
        this.Authority = new Uri(new Uri(this.container.GetBaseAddress()), "realms/talent");
    }

    /// <summary>Stops the container.</summary>
    public Task DisposeAsync() => this.container.DisposeAsync().AsTask();
}

/// <summary>Marks a test class as owning one dedicated Keycloak instance.</summary>
[CollectionDefinition(Name)]
public sealed class KeycloakOnlyCollection : ICollectionFixture<KeycloakOnlyFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "keycloak-only";
}
