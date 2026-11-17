using System.Security.Cryptography;

namespace RestaurantOrders.Cloud.Api;

public static class CloudPasswordHasher
{
    private const int Iterations = 600_000;
    private const int SaltLength = 16;
    private const int HashLength = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashLength);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string encoded)
    {
        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" ||
            !int.TryParse(parts[1], out var iterations) || iterations is < 100_000 or > 2_000_000)
            return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            if (salt.Length is < 16 or > 64 || expected.Length is < 24 or > 64) return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
