using System.Text.Json.Serialization;

namespace BlueBubbles.Core.Models;

public record FirebaseProjectList(
    [property: JsonPropertyName("results")] List<FirebaseProject>? Results
);

public record FirebaseProject(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("resources")] FirebaseResources? Resources
);

public record FirebaseResources(
    [property: JsonPropertyName("realtimeDatabaseInstance")] string? RealtimeDatabaseInstance
);
