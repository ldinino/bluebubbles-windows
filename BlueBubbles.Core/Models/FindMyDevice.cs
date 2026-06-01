using System.Text.Json.Serialization;

namespace BlueBubbles.Core.Models;

public record FindMyDevice(
    [property: JsonPropertyName("deviceModel")] string? DeviceModel,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("batteryStatus")] string? BatteryStatus,
    [property: JsonPropertyName("batteryLevel")] double? BatteryLevel,
    [property: JsonPropertyName("isConsideredAccessory")] bool IsConsideredAccessory,
    [property: JsonPropertyName("address")] FindMyAddress? Address,
    [property: JsonPropertyName("location")] FindMyLocation? Location,
    [property: JsonPropertyName("modelDisplayName")] string? ModelDisplayName,
    [property: JsonPropertyName("deviceDisplayName")] string? DeviceDisplayName,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("rawDeviceModel")] string? RawDeviceModel,
    [property: JsonPropertyName("baUUID")] string? BaUuid,
    [property: JsonPropertyName("deviceDiscoveryId")] string? DeviceDiscoveryId,
    [property: JsonPropertyName("deviceClass")] string? DeviceClass,
    [property: JsonPropertyName("deviceStatus")] string? DeviceStatus,
    [property: JsonPropertyName("isMac")] object? IsMac,
    [property: JsonPropertyName("passcodeLength")] int? PasscodeLength,
    [property: JsonPropertyName("maxMsgChar")] int? MaxMsgChar,
    [property: JsonPropertyName("features")] Dictionary<string, bool?>? Features,
    [property: JsonPropertyName("lostModeEnabled")] object? LostModeEnabled,
    [property: JsonPropertyName("lostModeCapable")] object? LostModeCapable,
    [property: JsonPropertyName("activationLocked")] object? ActivationLocked,
    [property: JsonPropertyName("locationEnabled")] object? LocationEnabled,
    [property: JsonPropertyName("locationCapable")] object? LocationCapable,
    [property: JsonPropertyName("thisDevice")] object? ThisDevice,
    [property: JsonPropertyName("lowPowerMode")] object? LowPowerMode
);

public record FindMyAddress(
    [property: JsonPropertyName("subAdministrativeArea")] string? SubAdministrativeArea,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("streetAddress")] string? StreetAddress,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("stateCode")] string? StateCode,
    [property: JsonPropertyName("administrativeArea")] string? AdministrativeArea,
    [property: JsonPropertyName("streetName")] string? StreetName,
    [property: JsonPropertyName("formattedAddressLines")] List<string>? FormattedAddressLines,
    [property: JsonPropertyName("mapItemFullAddress")] string? MapItemFullAddress,
    [property: JsonPropertyName("fullThroroughfare")] string? FullThoroughfare,
    [property: JsonPropertyName("locality")] string? Locality,
    [property: JsonPropertyName("country")] string? Country
);

public record FindMyLocation(
    [property: JsonPropertyName("positionType")] string? PositionType,
    [property: JsonPropertyName("verticalAccuracy")] int? VerticalAccuracy,
    [property: JsonPropertyName("longitude")] double? Longitude,
    [property: JsonPropertyName("floorLevel")] int? FloorLevel,
    [property: JsonPropertyName("isInaccurate")] bool? IsInaccurate,
    [property: JsonPropertyName("isOld")] bool? IsOld,
    [property: JsonPropertyName("horizontalAccuracy")] double? HorizontalAccuracy,
    [property: JsonPropertyName("latitude")] double? Latitude,
    [property: JsonPropertyName("timeStamp")] long? TimeStamp,
    [property: JsonPropertyName("altitude")] int? Altitude,
    [property: JsonPropertyName("locationFinished")] bool? LocationFinished
);
