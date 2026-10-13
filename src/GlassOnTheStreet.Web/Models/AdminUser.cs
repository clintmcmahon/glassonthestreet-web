using System.ComponentModel.DataAnnotations;

namespace GlassOnTheStreet.Web.Models;

/// <summary>An admin login. Only a salted PBKDF2 hash of the password is stored, never the password.</summary>
public class AdminUser
{
    public int Id { get; set; }

    [MaxLength(100)]
    public required string Username { get; set; }

    [MaxLength(300)]
    public required string PasswordHash { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? PasswordChangedAt { get; set; }
}
