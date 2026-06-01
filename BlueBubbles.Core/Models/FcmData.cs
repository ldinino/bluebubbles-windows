using System.Text.Json.Serialization;

namespace BlueBubbles.Core.Models;

public record FcmData(
    [property: JsonPropertyName("project_info")] FcmProjectInfo? ProjectInfo,
    [property: JsonPropertyName("client")] List<FcmClient>? Client
);

public record FcmProjectInfo(
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("storage_bucket")] string? StorageBucket,
    [property: JsonPropertyName("firebase_url")] string? FirebaseUrl
);

public record FcmClient(
    [property: JsonPropertyName("client_info")] FcmClientInfo? ClientInfo,
    [property: JsonPropertyName("oauth_client")] List<FcmOAuthClient>? OAuthClient,
    [property: JsonPropertyName("api_key")] List<FcmApiKey>? ApiKey
);

public record FcmClientInfo(
    [property: JsonPropertyName("mobilesdk_app_id")] string? MobileSdkAppId
);

public record FcmOAuthClient(
    [property: JsonPropertyName("client_id")] string? ClientId
);

public record FcmApiKey(
    [property: JsonPropertyName("current_key")] string? CurrentKey
);
