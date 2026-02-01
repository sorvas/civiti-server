namespace Civiti.Api.Services.Interfaces;

public interface ISupabaseService
{
    bool ValidateToken(string token);
    string? GetUserIdFromToken(string token);
    string? GetUserEmailFromToken(string token);
    Task<bool> CheckHealthAsync();
}