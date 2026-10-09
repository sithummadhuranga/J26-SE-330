namespace Sync.Common.Auth;

/// <summary>JWT claim names shared by the identity service and every resource server.</summary>
public static class ClaimNames
{
    public const string Subject = "sub";
    public const string DeviceId = "device_id";
    public const string FacilityId = "facility_id";
    public const string Role = "role";
    public const string ClientId = "client_id";
}
