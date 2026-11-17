using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ExcelDataReader;

namespace RestaurantOrders.Services;

public static class ExcelImportService
{
    public sealed class CatalogImportPreviewItem
    {
        public string Name { get; set; } = "";
        public string CategoryName { get; set; } = "";
        public decimal Price { get; set; }
        public int SourceRow { get; set; }
    }

    public sealed record CatalogImportParseResult(
        List<CatalogImportPreviewItem> Items,
        List<string> Warnings,
        bool UsedColumnHeaders);

    static ExcelImportService()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // ήδη καταχωρημένο, δεν πειράζει
        }
    }

    /// <summary>
    /// Διαβάζει ένα .xlsx αρχείο και επιστρέφει μία λίστα γραμμών, όπου κάθε γραμμή
    /// είναι ένα dictionary επικεφαλίδα-στήλης -> τιμή κελιού (η πρώτη γραμμή του
    /// αρχείου θεωρείται επικεφαλίδες).
    /// </summary>
    public static List<Dictionary<string, string>> ReadRows(Stream fileStream)
    {
        using var reader = ExcelReaderFactory.CreateReader(fileStream);
        var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration
        {
            ConfigureDataTable = _ => new ExcelDataTableConfiguration
            {
                UseHeaderRow = true
            }
        });

        var rows = new List<Dictionary<string, string>>();
        if (dataSet.Tables.Count == 0) return rows;

        var table = dataSet.Tables[0];
        foreach (DataRow row in table.Rows)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataColumn col in table.Columns)
            {
                dict[col.ColumnName.Trim()] = row[col]?.ToString()?.Trim() ?? "";
            }
            rows.Add(dict);
        }
        return rows;
    }

    /// <summary>
    /// Ψάχνει σε μια γραμμή για την πρώτη στήλη που ταιριάζει (χωρίς να πειράζει η
    /// διαφορά κεφαλαίων/πεζών) με οποιοδήποτε από τα δοθέντα ονόματα-υποψήφια.
    /// </summary>
    public static string GetField(Dictionary<string, string> row, params string[] candidateHeaders)
    {
        foreach (var candidate in candidateHeaders)
        {
            if (row.TryGetValue(candidate, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return "";
    }

    /// <summary>
    /// Parses both a conventional name/category/price table and a loose menu where
    /// each one-cell row names a category and following rows list products/prices.
    /// </summary>
    public static CatalogImportParseResult ReadCatalog(Stream fileStream)
    {
        using var reader = ExcelReaderFactory.CreateReader(fileStream);
        var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration
        {
            ConfigureDataTable = _ => new ExcelDataTableConfiguration { UseHeaderRow = false }
        });
        if (dataSet.Tables.Count == 0)
            return new(new(), new() { "Το αρχείο δεν περιέχει φύλλο με δεδομένα." }, false);

        var table = dataSet.Tables.Cast<DataTable>().FirstOrDefault(t => t.Rows.Cast<DataRow>()
            .Any(row => row.ItemArray.Any(value => !string.IsNullOrWhiteSpace(value?.ToString()))));
        if (table is null)
            return new(new(), new() { "Δεν βρέθηκαν δεδομένα σε κανένα φύλλο του αρχείου." }, false);
        var rows = new List<(int Number, List<string> Cells)>();
        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            var cells = new List<string>(table.Columns.Count);
            foreach (DataColumn column in table.Columns)
                cells.Add(table.Rows[rowIndex][column]?.ToString()?.Trim() ?? "");
            if (cells.Any(c => !string.IsNullOrWhiteSpace(c))) rows.Add((rowIndex + 1, cells));
        }

        if (rows.Count == 0)
            return new(new(), new() { "Το πρώτο φύλλο είναι κενό." }, false);

        var header = FindHeaderColumns(rows[0].Cells);
        return header.Name >= 0
            ? ParseHeaderedCatalog(rows, header)
            : ParseSectionedCatalog(rows);
    }

    private static (int Name, int Category, int Price) FindHeaderColumns(IReadOnlyList<string> cells)
    {
        var nameAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "name", "product", "item", "description", "productname", "itemname",
            "περιγραφη", "ονομα", "ονομαπροιοντος", "ονομαειδους", "προιον", "προιοντος", "ειδος"
        };
        var categoryAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "category", "categoryname", "group", "section", "κατηγορια", "ονομακατηγοριας", "ομαδα", "τμημα"
        };
        var priceAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "price", "unitprice", "saleprice", "cost", "τιμη", "τιμημοναδας", "τιμηπωλησης", "πωληση"
        };
        var name = -1; var category = -1; var price = -1;
        for (var i = 0; i < cells.Count; i++)
        {
            var key = NormalizeHeader(cells[i]);
            if (name < 0 && nameAliases.Contains(key)) name = i;
            if (category < 0 && categoryAliases.Contains(key)) category = i;
            if (price < 0 && priceAliases.Contains(key)) price = i;
        }
        return (name, category, price);
    }

    private static string NormalizeHeader(string value)
    {
        var normalized = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(ch))
                builder.Append(char.ToLowerInvariant(ch));
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static CatalogImportParseResult ParseHeaderedCatalog(
        List<(int Number, List<string> Cells)> rows, (int Name, int Category, int Price) columns)
    {
        var items = new List<CatalogImportPreviewItem>();
        var warnings = new List<string>();
        var currentCategory = "";
        var missingPriceRows = new List<int>();
        foreach (var (number, cells) in rows.Skip(1))
        {
            var name = Cell(cells, columns.Name);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var category = Cell(cells, columns.Category);
            if (!string.IsNullOrWhiteSpace(category)) currentCategory = category;
            var rawPrice = Cell(cells, columns.Price);
            var price = TryParseMoney(rawPrice, out var parsed) ? parsed : 0m;
            if (columns.Price >= 0 && string.IsNullOrWhiteSpace(rawPrice))
                missingPriceRows.Add(number);
            items.Add(new() { Name = name, CategoryName = currentCategory, Price = price, SourceRow = number });
        }
        if (missingPriceRows.Count > 0)
            warnings.Add($"Δεν βρέθηκε τιμή σε {missingPriceRows.Count} γραμμές (π.χ. {string.Join(", ", missingPriceRows.Take(8))}). Μπήκε 0,00 €· συμπλήρωσέ τη στην προεπισκόπηση.");
        if (columns.Price < 0 && items.Count > 0)
            warnings.Add("Δεν εντοπίστηκε στήλη τιμής. Τα προϊόντα μπήκαν με 0,00 € και μπορείς να συμπληρώσεις τις τιμές στην προεπισκόπηση.");
        if (columns.Category < 0 && items.Count > 0)
            warnings.Add("Δεν εντοπίστηκε στήλη κατηγορίας. Τα προϊόντα χωρίς επικεφαλίδες κατηγορίας θα εισαχθούν χωρίς κατηγορία.");
        if (items.Count == 0) warnings.Add("Δεν εντοπίστηκαν γραμμές προϊόντων κάτω από τις επικεφαλίδες.");
        return new(items, warnings, true);
    }

    private static CatalogImportParseResult ParseSectionedCatalog(List<(int Number, List<string> Cells)> rows)
    {
        var items = new List<CatalogImportPreviewItem>();
        var warnings = new List<string>();
        var currentCategory = "";
        var missingPriceRows = new List<int>();
        var rangedPriceRows = new List<int>();
        var previousRowNumber = 0;
        var startedCatalog = false;

        foreach (var (number, cells) in rows)
        {
            var blankRowBefore = previousRowNumber > 0 && number > previousRowNumber + 1;
            previousRowNumber = number;
            // Το συγκεκριμένο workbook βάζει extras/σημειώσεις στις στήλες J και μετά.
            // Για τη λίστα καταλόγου εξετάζουμε τις πρώτες οκτώ στήλες (A-H).
            var nonEmpty = cells.Take(8).Select((value, index) => (Value: value.Trim(), Index: index))
                .Where(x => !string.IsNullOrWhiteSpace(x.Value)).ToList();
            if (nonEmpty.Count == 0) continue;

            var categoryFromRow = FindKnownCategory(nonEmpty.Select(x => x.Value));
            if (!startedCatalog)
            {
                if (categoryFromRow is null) continue;
                startedCatalog = true;
            }

            if (categoryFromRow is not null && nonEmpty.All(x => !TrySplitInlinePrice(x.Value, out _, out _, out _)))
            {
                currentCategory = categoryFromRow;
                continue;
            }

            if (nonEmpty.Count == 1)
            {
                if (TrySplitInlinePrice(nonEmpty[0].Value, out var inlineName, out var inlinePrice, out var inlineWasRange))
                {
                    items.Add(new() { Name = inlineName, CategoryName = currentCategory, Price = inlinePrice, SourceRow = number });
                    if (inlineWasRange) rangedPriceRows.Add(number);
                    continue;
                }

                var heading = nonEmpty[0].Value;
                if (IsWorkbookTitle(heading) && string.IsNullOrWhiteSpace(currentCategory) && items.Count == 0)
                    continue;
                if (string.IsNullOrWhiteSpace(currentCategory) || blankRowBefore)
                    currentCategory = heading;
                else
                {
                    items.Add(new() { Name = heading, CategoryName = currentCategory, Price = 0m, SourceRow = number });
                    missingPriceRows.Add(number);
                }
                continue;
            }

            var priceCell = nonEmpty.LastOrDefault(x => TryParseMoney(x.Value, out _));
            var textCells = nonEmpty.Where(x => !TryParseMoney(x.Value, out _)).ToList();
            if (textCells.Count > 1 && !string.IsNullOrWhiteSpace(currentCategory) &&
                string.Equals(textCells[0].Value, currentCategory, StringComparison.OrdinalIgnoreCase))
                textCells.RemoveAt(0);
            var nameCell = textCells.FirstOrDefault();

            if (string.IsNullOrWhiteSpace(nameCell.Value))
            {
                warnings.Add($"Γραμμή {number}: δεν αναγνωρίστηκε όνομα προϊόντος και παραλείφθηκε.");
                continue;
            }

            var price = priceCell.Value is not null && TryParseMoney(priceCell.Value, out var parsedPrice) ? parsedPrice : 0m;
            if (priceCell.Value is null && TrySplitInlinePrice(nameCell.Value, out var splitName, out var splitPrice, out var wasRange))
            {
                nameCell = (splitName, nameCell.Index);
                price = splitPrice;
                if (wasRange) rangedPriceRows.Add(number);
            }
            if (priceCell.Value is null)
            {
                if (price == 0m) missingPriceRows.Add(number);
            }
            items.Add(new() { Name = nameCell.Value, CategoryName = currentCategory, Price = price, SourceRow = number });
        }

        if (missingPriceRows.Count > 0)
            warnings.Add($"Δεν αναγνωρίστηκε τιμή σε {missingPriceRows.Count} γραμμές (π.χ. {string.Join(", ", missingPriceRows.Take(8))}). Μπήκε 0,00 €· έλεγξε τις τιμές στην προεπισκόπηση.");
        if (rangedPriceRows.Count > 0)
            warnings.Add($"Σε {rangedPriceRows.Count} προϊόντα βρέθηκε εύρος τιμής (π.χ. 2-3 €). Για την προεπισκόπηση χρησιμοποιήθηκε η πρώτη τιμή· έλεγξέ τη πριν την εισαγωγή.");
        if (items.Count == 0)
            warnings.Add("Δεν βρέθηκαν προϊόντα. Στη μορφή λίστας, κάθε κατηγορία γράφεται σε δική της γραμμή και τα προϊόντα της στις επόμενες γραμμές, με την τιμή σε διπλανό κελί ή μαζί με το όνομα.");
        else if (items.Any(x => string.IsNullOrWhiteSpace(x.CategoryName)))
            warnings.Add("Κάποια προϊόντα δεν είχαν προηγούμενη επικεφαλίδα κατηγορίας και θα εισαχθούν χωρίς κατηγορία.");
        return new(items, warnings, false);
    }

    private static string Cell(IReadOnlyList<string> cells, int index) =>
        index >= 0 && index < cells.Count ? cells[index].Trim() : "";

    private static bool IsWorkbookTitle(string value) =>
        NormalizeHeader(value) is "catalog" or "menu" or "catalogue" or "τιμοκαταλογος" or "καταλογος" or "μενου" or "προιοντα";

    private static readonly HashSet<string> KnownCategoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "καφες", "καφεδες", "κυριως", "κυριωςπιατα", "ορεκτικα", "σαλατες", "γλυκα", "επιδορπια",
        "αναψυκτικα", "ποτα", "μπυρες", "κρασια", "ροφηματα", "πρωινο", "σνακ", "πιτσες", "πιτσα",
        "χυμοι", "ενεργειακα", "ενεργειακα ισοτονικα", "milk shakes/smoothies", "κοκτειλ", "coctail", "cocktail",
        "τσαι", "σοκολατες κρυες/ζεστες", "παροχες εκτυπωσεις", "γρανιτες", "φαγητο", "καφεςδες",
        "coffee", "coffees", "main", "maincourse", "maincourses", "appetizers", "starters", "salads",
        "desserts", "drinks", "beverages", "softdrinks", "beer", "wines", "breakfast", "snacks", "pizza", "pasta"
    };

    private static string? FindKnownCategory(IEnumerable<string> values) => values
        .Select(value => value.Trim())
        .FirstOrDefault(value => KnownCategoryNames.Any(known => NormalizeHeader(known) == NormalizeHeader(value)));

    private static bool TrySplitInlinePrice(string value, out string name, out decimal price, out bool wasRange)
    {
        name = ""; price = 0m; wasRange = false;
        var match = Regex.Match(value,
            @"^(.+?)\s*(?:[-–—:;|]\s*|\s+)(?:€\s*)?(\d+(?:[.,]\d{1,2})?)(?:(?:\s*[-–—]\s*|\s+)(\d+(?:[.,]\d{1,2})?))?\s*(?:€|eur)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !TryParseMoney(match.Groups[2].Value, out price)) return false;
        name = match.Groups[1].Value.Trim();
        wasRange = match.Groups[3].Success;
        return name.Length > 0;
    }

    private static bool TryParseMoney(string raw, out decimal price)
    {
        price = 0m;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var value = raw.Trim().Replace("€", "", StringComparison.OrdinalIgnoreCase)
            .Replace("EUR", "", StringComparison.OrdinalIgnoreCase).Replace(" ", "");
        if (value.Length == 0 || !Regex.IsMatch(value, @"^[-+]?\d[\d.,]*$", RegexOptions.CultureInvariant)) return false;

        var comma = value.LastIndexOf(',');
        var dot = value.LastIndexOf('.');
        if (comma >= 0 && dot >= 0)
        {
            var decimalSeparator = comma > dot ? ',' : '.';
            value = value.Replace(decimalSeparator == ',' ? "." : ",", "", StringComparison.Ordinal);
            if (decimalSeparator == ',') value = value.Replace(',', '.');
        }
        else if (comma >= 0)
            value = value.Replace(',', '.');

        return decimal.TryParse(value, NumberStyles.Number | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out price);
    }
}
