using System.Security.Cryptography;
using System.Text;

namespace GlassOnTheStreet.Web.Services;

/// <summary>
/// Salted PBKDF2-HMAC-SHA512 password hashing. The stored form is "v1.iterations.salt.hash" (base64),
/// so the cost can be raised later and old hashes still verify.
/// </summary>
public static class AdminPasswordHasher
{
    public const int MinPasswordLength = 14;

    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        return $"v1.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(Derive(password, salt, Iterations))}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 4 || parts[0] != "v1" || !int.TryParse(parts[1], out var iterations) || iterations < 1)
        {
            return false;
        }

        try
        {
            var expected = Convert.FromBase64String(parts[3]);
            return CryptographicOperations.FixedTimeEquals(Derive(password, Convert.FromBase64String(parts[2]), iterations, expected.Length), expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>A valid hash of a throwaway password, checked when the username is unknown so that case takes as long as a wrong password.</summary>
    public static readonly string Decoy = Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)));

    private static byte[] Derive(string password, byte[] salt, int iterations, int length = HashBytes) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA512, length);
}
