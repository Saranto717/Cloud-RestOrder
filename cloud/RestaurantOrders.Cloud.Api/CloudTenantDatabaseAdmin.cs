using Npgsql;
using System.Globalization;
using System.Text;

namespace RestaurantOrders.Cloud.Api;

/// <summary>Creates one isolated PostgreSQL database for each active cloud store.</summary>
public sealed class CloudTenantDatabaseAdmin(string adminConnectionString, string databaseOwner)
{
    public async Task EnsureStoreDatabaseAsync(string name, CancellationToken cancellationToken)
    {
        CloudTenantDatabaseName.Validate(name);
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection))
        {
            exists.Parameters.AddWithValue("name", name);
            if (await exists.ExecuteScalarAsync(cancellationToken) is not null) return;
        }

        // The database identifier is formed from a fixed prefix and a GUID.
        // CREATE DATABASE cannot run in a transaction or use parameters for names.
        var owner = databaseOwner.Replace("\"", "\"\"");
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\" OWNER \"{owner}\"", connection);
        try
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.DuplicateDatabase)
        {
            // A concurrent request or background worker created it first.
        }
    }
}

public static class CloudTenantDatabaseName
{
    public static string ForStore(string storeName, Guid storeId)
    {
        var normalized = storeName.Normalize(NormalizationForm.FormD);
        var slug = new StringBuilder();
        var separator = false;
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            foreach (var item in TransliterateGreek(character))
            {
                if (item is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')
                {
                    slug.Append(char.ToLowerInvariant(item));
                    separator = false;
                }
                else if (!separator && slug.Length > 0)
                {
                    slug.Append('_');
                    separator = true;
                }
            }
        }

        var cleanSlug = slug.ToString().Trim('_');
        if (cleanSlug.Length == 0) cleanSlug = "store";
        if (cleanSlug.Length > 38) cleanSlug = cleanSlug[..38].TrimEnd('_');
        var prefix = $"restaurant_{cleanSlug}_";
        var databaseName = prefix + storeId.ToString("N")[..12];
        Validate(databaseName);
        return databaseName;
    }

    public static void Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 63 ||
            !(name[0] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_') ||
            name.Any(character => !(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')))
            throw new ArgumentException("Το όνομα PostgreSQL βάσης δεν είναι έγκυρο.", nameof(name));
    }

    private static string TransliterateGreek(char character) => character switch
    {
        'α' or 'Α' => "a", 'β' or 'Β' => "v", 'γ' or 'Γ' => "g", 'δ' or 'Δ' => "d",
        'ε' or 'Ε' => "e", 'ζ' or 'Ζ' => "z", 'η' or 'Η' => "i", 'θ' or 'Θ' => "th",
        'ι' or 'Ι' => "i", 'κ' or 'Κ' => "k", 'λ' or 'Λ' => "l", 'μ' or 'Μ' => "m",
        'ν' or 'Ν' => "n", 'ξ' or 'Ξ' => "x", 'ο' or 'Ο' => "o", 'π' or 'Π' => "p",
        'ρ' or 'Ρ' => "r", 'σ' or 'Σ' or 'ς' => "s", 'τ' or 'Τ' => "t", 'υ' or 'Υ' => "y",
        'φ' or 'Φ' => "f", 'χ' or 'Χ' => "ch", 'ψ' or 'Ψ' => "ps", 'ω' or 'Ω' => "o",
        _ => character.ToString()
    };
}
