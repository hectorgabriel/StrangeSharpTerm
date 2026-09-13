using System.Text.Json;
using System.Text.Json.Serialization;
using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.Mcp;

/// <summary>
/// Connected tools, as a preferences file keeps them.
///
/// A shape of its own rather than serialising <see cref="McpServerConfig"/>
/// directly, so that renaming a property here is a decision about a file people
/// have on disk rather than an accident of refactoring — the same separation
/// <c>InventoryDocument</c> makes for the inventory.
///
/// No secret is in it. An HTTP token and any OAuth tokens live in the platform
/// store, and there is nowhere here to put one.
/// </summary>
public sealed record McpDocument
{
    public const string Key = "mcp";

    [JsonPropertyName("servers")]
    public IReadOnlyList<ServerEntry> Servers { get; init; } = [];

    [JsonPropertyName("offerInPanes")]
    public bool OfferInPanes { get; init; } = true;

    [JsonPropertyName("offerInRuns")]
    public bool OfferInRuns { get; init; }

    public sealed record ServerEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = "";

        [JsonPropertyName("name")]
        public string Name { get; init; } = "";

        [JsonPropertyName("transport")]
        public string Transport { get; init; } = "local";

        [JsonPropertyName("command")]
        public string Command { get; init; } = "";

        [JsonPropertyName("arguments")]
        public IReadOnlyList<string> Arguments { get; init; } = [];

        [JsonPropertyName("environment")]
        public IReadOnlyDictionary<string, string> Environment { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        [JsonPropertyName("url")]
        public string Url { get; init; } = "";

        [JsonPropertyName("enabled")]
        public bool Enabled { get; init; } = true;

        /// <summary>
        /// The standing passes a person granted at the gate. Kept because a
        /// grant that did not survive a relaunch would be asked for again every
        /// session, which teaches people to approve without reading.
        /// </summary>
        [JsonPropertyName("alwaysAllowed")]
        public IReadOnlyList<string> AlwaysAllowed { get; init; } = [];
    }

    public static McpDocument From(McpSettings settings) => new()
    {
        OfferInPanes = settings.OfferInPanes,
        OfferInRuns = settings.OfferInRuns,
        Servers =
        [
            .. settings.Servers.Select(server => new ServerEntry
            {
                Id = server.Id.Value.ToString(),
                Name = server.Name,
                Transport = server.Transport == McpTransport.Http ? "http" : "local",
                Command = server.Command,
                Arguments = server.Arguments,
                Environment = server.Environment,
                Url = server.Url,
                Enabled = server.IsEnabled,
                AlwaysAllowed = server.AlwaysAllowed,
            }),
        ],
    };

    public McpSettings ToSettings() => new()
    {
        OfferInPanes = OfferInPanes,
        OfferInRuns = OfferInRuns,
        Servers =
        [
            .. Servers.Select(entry => new McpServerConfig
            {
                // A file written by hand, or by a version that did not have
                // ids, still loads: a fresh one is as good as a remembered one
                // for everything except matching grants to servers.
                Id = Guid.TryParse(entry.Id, out var id) ? new NodeId(id) : NodeId.New(),
                Name = entry.Name,
                Transport = entry.Transport.Equals("http", StringComparison.OrdinalIgnoreCase)
                    ? McpTransport.Http
                    : McpTransport.Local,
                Command = entry.Command,
                Arguments = entry.Arguments,
                Environment = entry.Environment,
                Url = entry.Url,
                IsEnabled = entry.Enabled,
                AlwaysAllowed = entry.AlwaysAllowed,
            }),
        ],
    };

    /// <summary>Reads the section out of whatever the preferences file holds.</summary>
    public static McpSettings Read(JsonElement? section)
    {
        if (section is not { ValueKind: JsonValueKind.Object } stored)
            return new McpSettings();

        try
        {
            return (stored.Deserialize<McpDocument>() ?? new McpDocument()).ToSettings();
        }
        catch (JsonException)
        {
            // A section this version cannot read is not worth failing a launch
            // over. No servers is a safe reading of an unreadable list.
            return new McpSettings();
        }
    }
}
