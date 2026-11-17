using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using RestaurantOrders.Data;

namespace RestaurantOrders.Services;

public sealed class DatabaseExplorerColumn
{
    public string Name { get; init; } = "";
    public string DeclaredType { get; init; } = "";
    public bool NotNull { get; init; }
    public int PrimaryKeyOrder { get; init; }
    public bool Editable => PrimaryKeyOrder == 0 && !IsSensitive(Name) && !DeclaredType.Contains("BLOB", StringComparison.OrdinalIgnoreCase);
    public static bool IsSensitive(string name) => new[] { "password", "secret", "token", "codehash", "subscriptionkey", "signature", "apikey", "privatekey" }
        .Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));
}

public sealed class DatabaseExplorerCell
{
    public string Name { get; init; } = "";
    public string DeclaredType { get; init; } = "";
    public bool NotNull { get; init; }
    public bool IsPrimaryKey { get; init; }
    public bool Editable { get; init; }
    public string? OriginalValue { get; init; }
    public string? Value { get; set; }
}

public sealed class DatabaseExplorerRow
{
    public string TableName { get; init; } = "";
    public string KeyLabel { get; init; } = "";
    public string KeyJson { get; init; } = "{}";
    public string OriginalJson { get; init; } = "{}";
    public List<DatabaseExplorerCell> Cells { get; init; } = new();
}

public sealed class DatabaseSqlQueryResult
{
    public List<string> Columns { get; init; } = new();
    public List<List<string?>> Rows { get; init; } = new();
    public bool HasMore { get; set; }
}

public sealed class DatabaseExplorerService(IDbContextFactory<AppDbContext> dbFactory)
{
    private const int PageSize = 50;
    private const int SqlResultLimit = 500;
    private const int SqlTextLimit = 12000;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<List<string>> GetTablesAsync()
    {
        using var db = await dbFactory.CreateDbContextAsync();
        var appConnection = db.Database.GetDbConnection();
        // Use a dedicated, non-pooled, read-only connection. PRAGMA query_only
        // is connection-scoped; setting it on EF's pooled connection can leave
        // the next normal POS request unable to write after this page closes.
        var readOnlyConnectionString = new SqliteConnectionStringBuilder(appConnection.ConnectionString)
        {
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(readOnlyConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '__EF%' ORDER BY name COLLATE NOCASE";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

    public async Task<List<DatabaseExplorerRow>> SearchAsync(string tableName, string search, int offset = 0, int globalLimitPerTable = 20)
    {
        using var db = await dbFactory.CreateDbContextAsync();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        var tables = tableName == "*" ? await GetTablesOnConnectionAsync(connection) : new List<string> { tableName };
        var rows = new List<DatabaseExplorerRow>();
        foreach (var table in tables)
        {
            var columns = await GetColumnsAsync(connection, table);
            if (columns.Count == 0) continue;
            var visibleColumns = columns.Where(c => !DatabaseExplorerColumn.IsSensitive(c.Name)).ToList();
            var loginCodeHashColumn = table.Equals("Users", StringComparison.OrdinalIgnoreCase)
                ? columns.FirstOrDefault(c => c.Name.Equals("CodeHash", StringComparison.OrdinalIgnoreCase))
                : null;
            var searchable = visibleColumns.Where(c => c.DeclaredType.Contains("CHAR", StringComparison.OrdinalIgnoreCase) ||
                c.DeclaredType.Contains("TEXT", StringComparison.OrdinalIgnoreCase) || c.DeclaredType.Contains("CLOB", StringComparison.OrdinalIgnoreCase) ||
                c.DeclaredType.Contains("INT", StringComparison.OrdinalIgnoreCase) || c.DeclaredType.Contains("REAL", StringComparison.OrdinalIgnoreCase) ||
                c.DeclaredType.Contains("NUM", StringComparison.OrdinalIgnoreCase) || c.DeclaredType.Contains("DEC", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(c.DeclaredType)).ToList();
            var predicates = new List<string>();
            if (!string.IsNullOrWhiteSpace(search))
            {
                predicates.AddRange(searchable.Select(c => $"CAST({Quote(c.Name)} AS TEXT) LIKE $term ESCAPE '\\' COLLATE NOCASE"));
                if (predicates.Count == 0) continue;
            }
            var where = predicates.Count == 0 ? "" : " WHERE (" + string.Join(" OR ", predicates) + ")";
            var primaryKeys = columns.Where(c => c.PrimaryKeyOrder > 0).OrderBy(c => c.PrimaryKeyOrder).ToList();
            var orderBy = primaryKeys.Count == 0 ? "" : " ORDER BY " + string.Join(", ", primaryKeys.Select(c => Quote(c.Name) + " DESC"));
            var take = tableName == "*" ? globalLimitPerTable : PageSize;
            var selectedExpressions = visibleColumns.Select(c => Quote(c.Name)).ToList();
            if (loginCodeHashColumn is not null)
                selectedExpressions.Add($"CASE WHEN COALESCE(TRIM(CAST({Quote(loginCodeHashColumn.Name)} AS TEXT)), '') = '' THEN 0 ELSE 1 END");
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {string.Join(", ", selectedExpressions)} FROM {Quote(table)}{where}{orderBy} LIMIT $take OFFSET $offset";
            if (predicates.Count > 0)
            {
                var term = command.CreateParameter(); term.ParameterName = "$term"; term.Value = "%" + EscapeLike(search.Trim()) + "%"; command.Parameters.Add(term);
            }
            var takeParameter = command.CreateParameter(); takeParameter.ParameterName = "$take"; takeParameter.Value = take; command.Parameters.Add(takeParameter);
            var offsetParameter = command.CreateParameter(); offsetParameter.ParameterName = "$offset"; offsetParameter.Value = tableName == "*" ? 0 : Math.Max(0, offset); command.Parameters.Add(offsetParameter);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                var cells = new List<DatabaseExplorerCell>();
                var keys = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < visibleColumns.Count; i++)
                {
                    var column = visibleColumns[i];
                    var value = reader.IsDBNull(i) ? null : FormatValue(reader.GetValue(i));
                    values[column.Name] = value;
                    if (column.PrimaryKeyOrder > 0) keys[column.Name] = value;
                    cells.Add(new DatabaseExplorerCell
                    {
                        Name = column.Name,
                        DeclaredType = column.DeclaredType,
                        NotNull = column.NotNull,
                        IsPrimaryKey = column.PrimaryKeyOrder > 0,
                        Editable = column.Editable,
                        OriginalValue = value,
                        Value = value
                    });
                }
                if (loginCodeHashColumn is not null)
                {
                    var codeStatus = Convert.ToInt32(reader.GetValue(visibleColumns.Count), CultureInfo.InvariantCulture) == 0
                        ? "Δεν έχει οριστεί"
                        : "Ορισμένος";
                    cells.Add(new DatabaseExplorerCell
                    {
                        Name = "Κωδικός εισόδου",
                        DeclaredType = "Κατάσταση",
                        NotNull = true,
                        IsPrimaryKey = false,
                        Editable = false,
                        OriginalValue = codeStatus,
                        Value = codeStatus
                    });
                }
                rows.Add(new DatabaseExplorerRow
                {
                    TableName = table,
                    KeyLabel = keys.Count == 0 ? "χωρίς πρωτεύον κλειδί" : string.Join(" · ", keys.Select(x => $"{x.Key}={x.Value}")),
                    KeyJson = JsonSerializer.Serialize(keys, JsonOptions),
                    OriginalJson = JsonSerializer.Serialize(values, JsonOptions),
                    Cells = cells
                });
            }
            await reader.DisposeAsync();
            if (tableName != "*" && predicates.Count == 0 && primaryKeys.Count == 0)
                break;
        }
        return rows;
    }

    public async Task<DatabaseSqlQueryResult> ExecuteReadOnlySqlAsync(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        if (sql.Length > SqlTextLimit)
            throw new InvalidOperationException($"Το ερώτημα είναι μεγαλύτερο από {SqlTextLimit} χαρακτήρες.");
        ValidateSingleSelect(sql);

        using var db = await dbFactory.CreateDbContextAsync();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();

        // query_only is connection-scoped and prevents any write even if a
        // malformed query passes the front-end validation.
        await using (var readOnly = connection.CreateCommand())
        {
            readOnly.CommandText = "PRAGMA query_only = ON";
            await readOnly.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 10;
        await using var reader = await command.ExecuteReaderAsync();
        if (reader.FieldCount > 100)
            throw new InvalidOperationException("Το αποτέλεσμα έχει πάνω από 100 στήλες. Περιόρισε τις στήλες στο SELECT.");

        var result = new DatabaseSqlQueryResult();
        for (var i = 0; i < reader.FieldCount; i++)
            result.Columns.Add(reader.GetName(i));

        while (await reader.ReadAsync())
        {
            if (result.Rows.Count == SqlResultLimit)
            {
                result.HasMore = true;
                break;
            }

            var values = new List<string?>(reader.FieldCount);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var columnValue = reader.IsDBNull(i) ? null : FormatValue(reader.GetValue(i));
                if (DatabaseExplorerColumn.IsSensitive(reader.GetName(i)))
                    columnValue = "[Κρυφό]";
                else if (columnValue is { Length: > 4000 })
                    columnValue = columnValue[..4000] + "… [περικοπή]";
                values.Add(columnValue);
            }
            result.Rows.Add(values);
        }

        return result;
    }

    public async Task SaveRowAsync(string tableName, string keyJson, string originalJson, IReadOnlyDictionary<string, string?> edited, int userId)
    {
        if (tableName.Equals("DatabaseEditAudit", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Το ιστορικό αλλαγών είναι μόνο για ανάγνωση.");

        var keys = JsonSerializer.Deserialize<Dictionary<string, string?>>(keyJson, JsonOptions) ?? new();
        var original = JsonSerializer.Deserialize<Dictionary<string, string?>>(originalJson, JsonOptions) ?? new();
        if (keys.Count == 0) throw new InvalidOperationException("Ο πίνακας δεν έχει πρωτεύον κλειδί και δεν μπορεί να γίνει ασφαλής επεξεργασία.");

        using var db = await dbFactory.CreateDbContextAsync();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        var columns = await GetColumnsAsync(connection, tableName);
        var validTable = await GetTablesOnConnectionAsync(connection);
        if (!validTable.Contains(tableName, StringComparer.Ordinal)) throw new InvalidOperationException("Ο πίνακας δεν υπάρχει πλέον.");
        var visibleColumns = columns.Where(c => !DatabaseExplorerColumn.IsSensitive(c.Name)).ToList();
        var pkColumns = columns.Where(c => c.PrimaryKeyOrder > 0).OrderBy(c => c.PrimaryKeyOrder).ToList();
        if (pkColumns.Count == 0 || pkColumns.Any(c => !keys.ContainsKey(c.Name))) throw new InvalidOperationException("Δεν βρέθηκε έγκυρο πρωτεύον κλειδί.");

        var updatedValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in visibleColumns.Where(c => c.Editable))
        {
            if (!edited.TryGetValue(column.Name, out var value)) continue;
            original.TryGetValue(column.Name, out var oldValue);
            if (string.Equals(value ?? "", oldValue ?? "", StringComparison.Ordinal)) continue;
            updatedValues[column.Name] = ParseValue(value, column);
        }
        if (updatedValues.Count == 0) throw new InvalidOperationException("Δεν υπάρχουν αλλαγές για αποθήκευση.");

        await using var transaction = await connection.BeginTransactionAsync();
        var before = await ReadRowAsync(connection, tableName, visibleColumns, pkColumns, keys, transaction);
        if (before is null) throw new InvalidOperationException("Η εγγραφή δεν βρέθηκε. Κάνε ξανά αναζήτηση.");
        foreach (var column in visibleColumns)
        {
            original.TryGetValue(column.Name, out var expected);
            before.TryGetValue(column.Name, out var current);
            if (!string.Equals(expected ?? "", current ?? "", StringComparison.Ordinal))
                throw new InvalidOperationException("Η εγγραφή άλλαξε από άλλη ενέργεια μετά την αναζήτηση. Κάνε ξανά αναζήτηση πριν από την αποθήκευση.");
        }

        var assignments = string.Join(", ", updatedValues.Keys.Select((name, index) => $"{Quote(name)} = $v{index}"));
        var predicate = string.Join(" AND ", pkColumns.Select((c, index) => $"{Quote(c.Name)} IS $k{index}"));
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = $"UPDATE {Quote(tableName)} SET {assignments} WHERE {predicate}";
            var index = 0;
            foreach (var pair in updatedValues) AddParameter(update, "$v" + index++, pair.Value);
            for (var i = 0; i < pkColumns.Count; i++) AddParameter(update, "$k" + i, ParseValue(keys[pkColumns[i].Name], pkColumns[i]));
            var affected = await update.ExecuteNonQueryAsync();
            if (affected != 1) throw new InvalidOperationException("Δεν αποθηκεύτηκε μία μοναδική εγγραφή. Έλεγξε ξανά τα κλειδιά.");
        }
        var after = await ReadRowAsync(connection, tableName, visibleColumns, pkColumns, keys, transaction);
        if (after is null) throw new InvalidOperationException("Η εγγραφή δεν βρέθηκε μετά την αποθήκευση.");
        await using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = "INSERT INTO DatabaseEditAudit (At, UserId, TableName, PrimaryKeyJson, BeforeJson, AfterJson) VALUES ($at, $user, $table, $pk, $before, $after)";
            AddParameter(audit, "$at", DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
            AddParameter(audit, "$user", userId);
            AddParameter(audit, "$table", tableName);
            AddParameter(audit, "$pk", JsonSerializer.Serialize(keys, JsonOptions));
            AddParameter(audit, "$before", JsonSerializer.Serialize(before, JsonOptions));
            AddParameter(audit, "$after", JsonSerializer.Serialize(after, JsonOptions));
            await audit.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private static async Task<List<string>> GetTablesOnConnectionAsync(DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '__EF%' ORDER BY name COLLATE NOCASE";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

    private static async Task<List<DatabaseExplorerColumn>> GetColumnsAsync(DbConnection connection, string tableName)
    {
        if (!(await GetTablesOnConnectionAsync(connection)).Contains(tableName, StringComparer.Ordinal)) return new();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({Quote(tableName)})";
        var columns = new List<DatabaseExplorerColumn>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(new DatabaseExplorerColumn
        {
            Name = reader.GetString(1),
            DeclaredType = reader.IsDBNull(2) ? "" : reader.GetString(2),
            NotNull = reader.GetInt32(3) != 0,
            PrimaryKeyOrder = reader.GetInt32(5)
        });
        return columns;
    }

    private static async Task<Dictionary<string, string?>?> ReadRowAsync(DbConnection connection, string table,
        List<DatabaseExplorerColumn> columns, List<DatabaseExplorerColumn> keys, Dictionary<string, string?> keyValues, DbTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {string.Join(", ", columns.Select(c => Quote(c.Name)))} FROM {Quote(table)} WHERE {string.Join(" AND ", keys.Select((c, i) => $"{Quote(c.Name)} IS $k{i}"))} LIMIT 1";
        for (var i = 0; i < keys.Count; i++) AddParameter(command, "$k" + i, ParseValue(keyValues[keys[i].Name], keys[i]));
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++) result[columns[i].Name] = reader.IsDBNull(i) ? null : FormatValue(reader.GetValue(i));
        return result;
    }

    private static object? ParseValue(string? text, DatabaseExplorerColumn column)
    {
        if (string.IsNullOrWhiteSpace(text) && !column.NotNull) return DBNull.Value;
        var type = column.DeclaredType.ToUpperInvariant();
        if (type.Contains("INT")) return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) ? integer : throw new InvalidOperationException($"Η τιμή της στήλης {column.Name} πρέπει να είναι ακέραιος αριθμός.");
        if (type.Contains("REAL") || type.Contains("FLOA") || type.Contains("DOUB")) return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) ? real : throw new InvalidOperationException($"Η τιμή της στήλης {column.Name} πρέπει να είναι αριθμός.");
        if (type.Contains("NUM") || type.Contains("DEC")) return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : throw new InvalidOperationException($"Η τιμή της στήλης {column.Name} πρέπει να είναι αριθμός.");
        return text ?? "";
    }

    private static string? FormatValue(object value) => value switch
    {
        DBNull => null,
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    private static string EscapeLike(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static void ValidateSingleSelect(string sql)
    {
        var firstKeyword = FirstSqlKeyword(sql);
        if (!string.Equals(firstKeyword, "SELECT", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(firstKeyword, "WITH", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Επιτρέπονται μόνο ερωτήματα SELECT ή WITH ... SELECT.");

        var quote = '\0';
        var lineComment = false;
        var blockComment = false;
        for (var i = 0; i < sql.Length; i++)
        {
            var current = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            if (lineComment)
            {
                if (current is '\r' or '\n') lineComment = false;
                continue;
            }
            if (blockComment)
            {
                if (current == '*' && next == '/') { blockComment = false; i++; }
                continue;
            }
            if (quote != '\0')
            {
                if (current == quote)
                {
                    if (next == quote) i++;
                    else quote = '\0';
                }
                else if (quote == ']' && current == ']') quote = '\0';
                continue;
            }
            if (current == '-' && next == '-') { lineComment = true; i++; continue; }
            if (current == '/' && next == '*') { blockComment = true; i++; continue; }
            if (current is '\'' or '"' or '`') { quote = current; continue; }
            if (current == '[') { quote = ']'; continue; }
            if (current == ';' && !string.IsNullOrWhiteSpace(sql[(i + 1)..]))
                throw new InvalidOperationException("Εκτέλεσε ένα μόνο SQL ερώτημα κάθε φορά· αφαίρεσε τυχόν δεύτερη εντολή μετά το ;.");
        }
    }

    private static string FirstSqlKeyword(string sql)
    {
        var index = 0;
        while (index < sql.Length)
        {
            while (index < sql.Length && char.IsWhiteSpace(sql[index])) index++;
            if (index + 1 < sql.Length && sql[index] == '-' && sql[index + 1] == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] is not '\r' and not '\n') index++;
                continue;
            }
            if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
            {
                var close = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (close < 0) throw new InvalidOperationException("Δεν έκλεισε ένα σχόλιο στο SQL.");
                index = close + 2;
                continue;
            }
            break;
        }

        var start = index;
        while (index < sql.Length && (char.IsLetter(sql[index]) || sql[index] == '_')) index++;
        return sql[start..index];
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter);
    }
}
