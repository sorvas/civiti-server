using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
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
    /// Supports both JWKS and legacy symmetric key validation
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

        // Configure token validation parameters
        jwtBearerOptions.TokenValidationParameters = new TokenValidationParameters
        {
            // Issuer validation
            ValidateIssuer = true,
            ValidIssuer = jwtValidationOptions.ValidIssuer,

            // Audience validation
            ValidateAudience = true,
            ValidAudience = jwtValidationOptions.ValidAudience,

            // Lifetime validation
            ValidateLifetime = true,
            ClockSkew = jwtValidationOptions.ClockSkew,

            // Signing key validation
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
                        var jwk = jwkTask.GetAwaiter().GetResult(); // Safe in this context as it's cached

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

        // Configure legacy fallback if enabled and secret is available
        if (jwtValidationOptions.EnableLegacyFallback &&
            !string.IsNullOrWhiteSpace(jwtValidationOptions.LegacyJwtSecret))
        {
            logger.LogInformation("Legacy JWT secret fallback is enabled");

            // Store the original resolver
            var originalResolver = jwtBearerOptions.TokenValidationParameters.IssuerSigningKeyResolver;

            // Create a composite resolver that tries JWKS first, then legacy
            jwtBearerOptions.TokenValidationParameters.IssuerSigningKeyResolver = (token, securityToken, kid, validationParameters) =>
            {
                // First try JWKS
                var jwksKeys = originalResolver?.Invoke(token, securityToken, kid, validationParameters);
                if (jwksKeys != null && jwksKeys.Any())
                {
                    return jwksKeys;
                }

                // Fallback to legacy symmetric key
                logger.LogDebug("JWKS validation failed, trying legacy symmetric key");
                var legacyKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtValidationOptions.LegacyJwtSecret));
                return new[] { legacyKey };
            };
        }

        // Configure JWT Bearer events for comprehensive logging
        jwtBearerOptions.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                var token = context.Request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "");
                var tokenSnippet = token?.Length > 20 ? token[..20] + "..." : token;

                logger.LogWarning("JWT authentication failed for token {TokenSnippet}: {Error}",
                    tokenSnippet, context.Exception?.Message);

                // Log additional details for debugging
                if (context.Exception is SecurityTokenValidationException validationEx)
                {
                    logger.LogDebug("JWT validation details: {Details}", validationEx.Message);
                }

                return Task.CompletedTask;
            },

            OnTokenValidated = context =>
            {
                var userId = context.Principal?.FindFirst("sub")?.Value;
                var issuer = context.Principal?.FindFirst("iss")?.Value;
                var audience = context.Principal?.FindFirst("aud")?.Value;

                logger.LogDebug("JWT token validated successfully - UserId: {UserId}, Issuer: {Issuer}, Audience: {Audience}",
                    userId, issuer, audience);

                return Task.CompletedTask;
            },

            OnChallenge = context =>
            {
                logger.LogWarning("JWT authentication challenge: {Error} - {ErrorDescription}",
                    context.Error, context.ErrorDescription);

                // Customize the challenge response if needed
                if (string.IsNullOrEmpty(context.Error))
                {
                    context.Error = "invalid_token";
                    context.ErrorDescription = "The access token is invalid or expired";
                }

                return Task.CompletedTask;
            },

            OnMessageReceived = context =>
            {
                // Optional: Extract token from custom locations (e.g., query string, cookies)
                // This is useful for WebSocket connections or special scenarios
                var token = context.Request.Query["access_token"].FirstOrDefault();
                if (!string.IsNullOrEmpty(token))
                {
                    context.Token = token;
                    logger.LogDebug("JWT token extracted from query string");
                }

                return Task.CompletedTask;
            }
        };

        logger.LogInformation("JWT Bearer authentication configured with JWKS support - Issuer: {Issuer}, Audience: {Audience}",
            jwtValidationOptions.ValidIssuer, jwtValidationOptions.ValidAudience);
    }
}