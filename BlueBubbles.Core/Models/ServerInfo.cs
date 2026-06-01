using System.Text.Json.Serialization;

namespace BlueBubbles.Core.Models;

public record ServerInfo(
    [property: JsonPropertyName("os_version")] string? OsVersion,
    [property: JsonPropertyName("server_version")] string? ServerVersion,
    [property: JsonPropertyName("private_api")] bool PrivateApi,
    [property: JsonPropertyName("helper_connected")] bool HelperConnected,
    [property: JsonPropertyName("proxy_service")] string? ProxyService,
    [property: JsonPropertyName("detected_icloud")] string? DetectedIcloud,
    [property: JsonPropertyName("local_ipv4s")] List<string>? LocalIpv4s,
    [property: JsonPropertyName("local_ipv6s")] List<string>? LocalIpv6s,
    [property: JsonPropertyName("platform")] string? Platform
);
