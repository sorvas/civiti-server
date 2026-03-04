namespace Civiti.Api.Infrastructure.Configuration;

public class SupabaseConfiguration
{
    public string Url { get; set; } = string.Empty;
    public string AnonKey { get; set; } = string.Empty;
    public string ServiceRoleKey { get; set; } = string.Empty;
    public bool HasServiceRoleKey => !string.IsNullOrWhiteSpace(ServiceRoleKey);
}