using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Civica.Api.Infrastructure.Configuration;
using Civica.Api.Services.Interfaces;

namespace Civica.Api.Infrastructure.Extensions;

/// <summary>
/// Extension methods for configuring JWT Bearer authentication with JWKS support
/// </summary>
public static class JwtBearerExtensions
{
    /// <summary>
    /// Configures JWT Bearer authentication with JWKS-based validation
    /// </summary>
    /// <param name="jwtBearerOptions">JWT Bearer options to configure</param>
    /// <param name="jwtValidationOptions">JWKS validation options</param>
    /// <param name="jwksManager">JWKS manager service</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="isDevelopment">Whether running in development environment</param>
    public static void ConfigureWithJwks(
        this JwtBearerOptions jwtBearerOptions,
        JwtValidationOptions jwtValidationOptions,
        IJwksManager jwksManager,
        ILogger logger,
        bool isDevelopment = false)
    {
        // Basic JWT Bearer configuration
        jwtBearerOptions.RequireHttpsMetadata = jwtValidationOptions.RequireHttpsMetadata && !isDevelopment;
        jwtBearerOptions.SaveToken = true;
        jwtBearerOptions.MapInboundClaims = false;

        // Configure token validation parameters
        jwtBearerOptions.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtValidationOptions.ValidIssuer,
            ValidateAudience = true,
            ValidAudience = jwtValidationOptions.ValidAudience,
            ValidateLifetime = true,
            ClockSkew = jwtValidationOptions.ClockSkew,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,

            // JWKS-based key resolver
            IssuerSigningKeyResolver = (token, securityToken, kid, validationParameters) =>
            {
                logger.LogDebug("Resolving signing key for kid: {Kid}", kid);

                try
                {
                    // Try to get the key by kid from JWKS
                    if (!string.IsNullOrEmpty(kid))
                    {
                        var jwkTask = jwksManager.GetKeyForKidAsync(kid, CancellationToken.None);
                        var jwk = jwkTask.GetAwaiter().GetResult();

                        if (jwk != null)
                        {
                            logger.LogDebug("Found JWKS key for kid: {Kid}", kid);
                            return new[] { jwk };
                        }
                    }

                    // Fallback: get all signing keys
                    logger.LogDebug("Kid not found or empty, trying all signing keys");
                    var allKeysTask = jwksManager.GetSigningKeysAsync(CancellationToken.None);
                    var allKeys = allKeysTask.GetAwaiter().GetResult();

                    var keysList = allKeys.ToList();
                    if (keysList.Count > 0)
                    {
                        logger.LogDebug("Returning {Count} signing keys for validation", keysList.Count);
                        return keysList;
                    }

                    logger.LogWarning("No JWKS keys available for validation");
                    return Enumerable.Empty<SecurityKey>();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error resolving JWKS signing key for kid: {Kid}", kid);
                    return Enumerable.Empty<SecurityKey>();
                }
            }
        };

        // Configure JWT Bearer events for logging
        jwtBearerOptions.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                logger.LogWarning("JWT authentication failed: {Error}", context.Exception?.Message);
                return Task.CompletedTask;
            }
        };

        logger.LogInformation("JWT Bearer authentication configured with JWKS support - Issuer: {Issuer}, Audience: {Audience}",
            jwtValidationOptions.ValidIssuer, jwtValidationOptions.ValidAudience);
    }
}