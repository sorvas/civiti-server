using System.ComponentModel.DataAnnotations;

namespace Civiti.Api.Models.Requests.Auth;

/// <summary>
/// Request to permanently delete a user account. Requires explicit confirmation.
/// </summary>
public class DeleteAccountRequest
{
    /// <summary>
    /// Must be exactly "DELETE" to confirm account deletion
    /// </summary>
    /// <example>DELETE</example>
    [Required]
    public string Confirmation { get; set; } = string.Empty;
}
