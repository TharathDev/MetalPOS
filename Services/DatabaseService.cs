using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using PosApp.Models;

namespace PosApp.Services;

/// <summary>
/// Owns the local SQLite database (metals_pos.db). Responsible for creating the
/// schema on first run, seeding sample metal products, and all read/write access
/// including full inventory CRUD and sale recording.
/// </summary>
public class DatabaseService
{
    private readonly string _connectionString;

    public DatabaseService()
    {
        // Store the DB next to the running executable so it is self-contained.
        var dbPath = Path.Combine(AppContext.BaseDirectory, "metals_pos.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath
        }.ToString();
    }

    /// <summary>Full path to the SQLite file, useful for logging/diagnostics.</summary>
    public string DatabasePath => Path.Combine(AppContext.BaseDirectory, "metals_pos.db");

    /// <summary>
    /// CREATE TABLE statements defining the schema. Applied before any column
    /// migration, because indexes may reference newly added columns.
    /// </summary>
    public static readonly string[] TableStatements =
    {
        @"CREATE TABLE IF NOT EXISTS Products (
            Id        INTEGER PRIMARY KEY AUTOINCREMENT,
            Category  TEXT    NOT NULL DEFAULT '',
            Name      TEXT    NOT NULL,
            Dimension TEXT    NOT NULL DEFAULT '',
            Unit      TEXT    NOT NULL DEFAULT 'ea',
            Barcode   TEXT,
            Price     REAL    NOT NULL DEFAULT 0,
            Stock     INTEGER NOT NULL DEFAULT 0
        )",
        @"CREATE TABLE IF NOT EXISTS Sales (
            Id              INTEGER PRIMARY KEY AUTOINCREMENT,
            ReceiptNo       TEXT     NOT NULL DEFAULT '',
            Timestamp       DATETIME NOT NULL,
            CustomerName    TEXT     NOT NULL DEFAULT '',
            CustomerPhone   TEXT     NOT NULL DEFAULT '',
            CustomerAddress TEXT     NOT NULL DEFAULT '',
            Note            TEXT     NOT NULL DEFAULT '',
            Subtotal        REAL     NOT NULL DEFAULT 0,
            Discount        REAL     NOT NULL DEFAULT 0,
            TaxRate         REAL     NOT NULL DEFAULT 0,
            TaxAmount       REAL     NOT NULL DEFAULT 0,
            TotalAmount     REAL     NOT NULL,
            AmountPaid      REAL     NOT NULL DEFAULT 0,
            ChangeDue       REAL     NOT NULL DEFAULT 0,
            PaymentMethod   TEXT     NOT NULL
        )",
        // No foreign key on ProductId: sale history must survive a product being
        // edited or deleted, so the description is copied onto the line instead.
        @"CREATE TABLE IF NOT EXISTS SaleItems (
            Id        INTEGER PRIMARY KEY AUTOINCREMENT,
            SaleId    INTEGER NOT NULL,
            ProductId INTEGER NOT NULL DEFAULT 0,
            Material  TEXT    NOT NULL DEFAULT '',
            Dimension TEXT    NOT NULL DEFAULT '',
            Unit      TEXT    NOT NULL DEFAULT 'ea',
            Quantity  INTEGER NOT NULL,
            UnitPrice REAL    NOT NULL,
            LineTotal REAL    NOT NULL DEFAULT 0,
            FOREIGN KEY (SaleId) REFERENCES Sales(Id)
        )",
    };

    /// <summary>
    /// Index statements, applied only after the column migration has run so they
    /// can safely reference columns added to an older database.
    /// </summary>
    public static readonly string[] IndexStatements =
    {
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_Sales_ReceiptNo ON Sales(ReceiptNo) WHERE ReceiptNo <> ''",
        "CREATE INDEX IF NOT EXISTS IX_SaleItems_SaleId ON SaleItems(SaleId)",
    };

    /// <summary>
    /// Full schema (tables then indexes), used by the remote backup which drops
    /// and recreates its tables on every sync.
    /// </summary>
    public static string[] SchemaStatements =>
        TableStatements.Concat(IndexStatements).ToArray();

    /// <summary>Tables that are included in the remote backup, in FK-safe insert order.</summary>
    public static readonly string[] BackupTables = { "Products", "Sales", "SaleItems" };

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    /// <summary>
    /// Creates all tables if they do not exist and seeds sample products when empty.
    /// Call this once at application startup.
    /// </summary>
    public void Initialize()
    {
        using var connection = OpenConnection();

        // 1. Tables first (a no-op when they already exist).
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = string.Join(";\n", TableStatements) + ";";
            cmd.ExecuteNonQuery();
        }

        // 2. Then add any columns missing from an older database.
        MigrateExistingSchema(connection);

        // 3. Indexes last, so they can reference the newly added columns.
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = string.Join(";\n", IndexStatements) + ";";
            cmd.ExecuteNonQuery();
        }

        SeedProductsIfEmpty(connection);
    }

    /// <summary>
    /// Brings a database created by an earlier version up to the current schema by
    /// adding any missing columns, then backfills receipt numbers for sales that
    /// pre-date the numbering scheme. Safe to run on every startup.
    /// </summary>
    private static void MigrateExistingSchema(SqliteConnection connection)
    {
        AddMissingColumns(connection, "Sales", new (string Name, string Definition)[]
        {
            ("ReceiptNo",       "TEXT NOT NULL DEFAULT ''"),
            ("CustomerName",    "TEXT NOT NULL DEFAULT ''"),
            ("CustomerPhone",   "TEXT NOT NULL DEFAULT ''"),
            ("CustomerAddress", "TEXT NOT NULL DEFAULT ''"),
            ("Note",            "TEXT NOT NULL DEFAULT ''"),
            ("Subtotal",        "REAL NOT NULL DEFAULT 0"),
            ("Discount",        "REAL NOT NULL DEFAULT 0"),
            ("TaxRate",         "REAL NOT NULL DEFAULT 0"),
            ("TaxAmount",       "REAL NOT NULL DEFAULT 0"),
            ("AmountPaid",      "REAL NOT NULL DEFAULT 0"),
            ("ChangeDue",       "REAL NOT NULL DEFAULT 0"),
        });

        AddMissingColumns(connection, "SaleItems", new (string Name, string Definition)[]
        {
            ("Material",  "TEXT NOT NULL DEFAULT ''"),
            ("Dimension", "TEXT NOT NULL DEFAULT ''"),
            ("Unit",      "TEXT NOT NULL DEFAULT 'ea'"),
            ("LineTotal", "REAL NOT NULL DEFAULT 0"),
        });

        // Older rows stored only a total; treat it as the subtotal so the money
        // breakdown at least adds up when the receipt is reprinted.
        using (var fix = connection.CreateCommand())
        {
            fix.CommandText = @"
                UPDATE Sales SET Subtotal = TotalAmount WHERE Subtotal = 0 AND TotalAmount <> 0;
                UPDATE Sales SET AmountPaid = TotalAmount WHERE AmountPaid = 0 AND TotalAmount <> 0;
                UPDATE SaleItems SET LineTotal = Quantity * UnitPrice WHERE LineTotal = 0;";
            fix.ExecuteNonQuery();
        }

        BackfillReceiptNumbers(connection);
    }

    private static void AddMissingColumns(
        SqliteConnection connection, string table, (string Name, string Definition)[] columns)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table});";
            using var reader = info.ExecuteReader();
            while (reader.Read())
                existing.Add(reader.GetString(1));
        }

        foreach (var (name, definition) in columns)
        {
            if (existing.Contains(name))
                continue;
            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {name} {definition};";
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>Assigns receipt numbers to any sale that does not have one yet.</summary>
    private static void BackfillReceiptNumbers(SqliteConnection connection)
    {
        var pending = new List<(long Id, DateTime Timestamp)>();
        using (var find = connection.CreateCommand())
        {
            find.CommandText =
                "SELECT Id, Timestamp FROM Sales WHERE ReceiptNo IS NULL OR ReceiptNo = '' ORDER BY Timestamp, Id;";
            using var reader = find.ExecuteReader();
            while (reader.Read())
                pending.Add((reader.GetInt64(0), reader.GetDateTime(1)));
        }

        if (pending.Count == 0)
            return;

        using var transaction = connection.BeginTransaction();
        foreach (var (id, timestamp) in pending)
        {
            var receiptNo = NextReceiptNo(connection, transaction, timestamp);
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE Sales SET ReceiptNo = $no WHERE Id = $id;";
            update.Parameters.AddWithValue("$no", receiptNo);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>
    /// Builds the next receipt number for the given day: yyyyMMdd followed by a
    /// 3-digit sequence that restarts at 001 each day (e.g. 20260731001).
    /// Must be called inside the same transaction as the sale insert so two
    /// concurrent sales cannot claim the same number.
    /// </summary>
    private static string NextReceiptNo(
        SqliteConnection connection, SqliteTransaction? transaction, DateTime timestamp)
    {
        var day = timestamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            SELECT COALESCE(MAX(CAST(SUBSTR(ReceiptNo, 9) AS INTEGER)), 0)
            FROM Sales
            WHERE LENGTH(ReceiptNo) >= 11 AND SUBSTR(ReceiptNo, 1, 8) = $day;";
        cmd.Parameters.AddWithValue("$day", day);

        var last = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        var next = last + 1;

        // Keeps 3 digits normally, and simply grows past 999 in a very busy day.
        return day + next.ToString("D3", CultureInfo.InvariantCulture);
    }

    private static void SeedProductsIfEmpty(SqliteConnection connection)
    {
        using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText = "SELECT COUNT(*) FROM Products;";
            var count = Convert.ToInt64(countCmd.ExecuteScalar());
            if (count > 0)
                return;
        }

        var samples = new (string Category, string Name, string Dimension, string Unit, string Sku, double Price, int Stock)[]
        {
            ("Steel", "Alloy Steel Grade A36", "2\" x 4\"",   "sheet", "STL-A36-24",  124.50, 48),
            ("Steel", "Alloy Steel Grade A36", "4\" x 8\"",   "sheet", "STL-A36-48",  286.00, 12),
            ("Steel", "Alloy Steel Grade A36", "12\" x 24\"", "sheet", "STL-A36-1224",890.25, 5),
            ("Iron",  "Cast Iron Grade 65-45-12", "1\" Pipe (per ft)", "ft",   "IRN-P1",  18.75, 120),
            ("Iron",  "Cast Iron Grade 65-45-12", "2\" Pipe (per ft)", "ft",   "IRN-P2",  32.40, 64),
            ("Iron",  "Cast Iron Grade 65-45-12", "Ornamental Casting","ea",   "IRN-ORN", 145.00, 9),
            ("Roofing", "Galvanized Corrugated G90", "26ga Sheet 3' x 8'",  "sheet", "ROF-26", 42.90, 210),
            ("Roofing", "Galvanized Corrugated G90", "Zinc Sheet 4' x 10'", "sheet", "ROF-ZN", 96.50, 3),
            ("Roofing", "Galvanized Corrugated G90", "Ridge Cap (per ft)",  "ft",    "ROF-RC", 7.25, 88),
            ("Tools", "Industrial Power Tools", "Angle Grinder 4.5\"", "ea", "TL-AG45", 79.99, 34),
            ("Tools", "Industrial Power Tools", "MIG Welder 180A",     "ea", "TL-MIG",  549.00, 6),
            ("Tools", "Industrial Power Tools", "Plasma Cutter 40A",   "ea", "TL-PLZ",  720.00, 4),
            ("Hardware", "Fasteners & Fittings", "1/2\" Hex Bolt (box)", "box",  "HW-HB", 24.00, 500),
            ("Hardware", "Fasteners & Fittings", "3/8\" Anchor (box)",   "box",  "HW-AN", 31.50, 320),
            ("Hardware", "Fasteners & Fittings", "Heavy Hinge (pair)",   "pair", "HW-HG", 18.90, 76),
        };

        using var transaction = connection.BeginTransaction();
        foreach (var s in samples)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"
                INSERT INTO Products (Category, Name, Dimension, Unit, Barcode, Price, Stock)
                VALUES ($cat, $name, $dim, $unit, $sku, $price, $stock);";
            insert.Parameters.AddWithValue("$cat", s.Category);
            insert.Parameters.AddWithValue("$name", s.Name);
            insert.Parameters.AddWithValue("$dim", s.Dimension);
            insert.Parameters.AddWithValue("$unit", s.Unit);
            insert.Parameters.AddWithValue("$sku", s.Sku);
            insert.Parameters.AddWithValue("$price", s.Price);
            insert.Parameters.AddWithValue("$stock", s.Stock);
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    // ==================== Inventory reads ====================

    /// <summary>
    /// Returns categories with SKU counts and total stock, for the dashboard cards.
    /// </summary>
    public List<CategoryInfo> GetCategories()
    {
        var results = new List<CategoryInfo>();
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT Category, COUNT(*) AS Skus, COALESCE(SUM(Stock), 0) AS TotalStock
            FROM Products
            WHERE Category <> ''
            GROUP BY Category
            ORDER BY Category;";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            results.Add(new CategoryInfo
            {
                Name = name,
                Description = DescribeCategory(name),
                SkuCount = reader.GetInt32(1),
                TotalStock = reader.GetInt32(2),
            });
        }
        return results;
    }

    private static string DescribeCategory(string name) => name switch
    {
        "Steel" => "H-Beams, Rebar, Sheets, and Structural Steel Components.",
        "Iron" => "Cast Iron Pipes, Ornaments, and Raw Industrial Castings.",
        "Roofing" => "Corrugated Sheets, Shingles, Gutters, and Flashing.",
        "Tools" => "Industrial Cutting, Welding Equipment, and Power Tools.",
        "Hardware" => "Fasteners, Bolts, Hinges, and Small Metal Components.",
        _ => "Custom metal stock and specialty components.",
    };

    /// <summary>
    /// Returns products matching the search term against category, name, dimension
    /// or SKU. An empty search term returns all products ordered by category/name.
    /// </summary>
    public List<Product> SearchProducts(string? searchTerm)
    {
        var results = new List<Product>();
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            cmd.CommandText =
                "SELECT Id, Category, Name, Dimension, Unit, Barcode, Price, Stock FROM Products ORDER BY Category, Name, Dimension;";
        }
        else
        {
            cmd.CommandText = @"
                SELECT Id, Category, Name, Dimension, Unit, Barcode, Price, Stock
                FROM Products
                WHERE Category LIKE $term OR Name LIKE $term OR Dimension LIKE $term OR Barcode LIKE $term
                ORDER BY Category, Name, Dimension;";
            cmd.Parameters.AddWithValue("$term", "%" + searchTerm.Trim() + "%");
        }

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadProduct(reader));

        return results;
    }

    /// <summary>Returns all products in a given category, ordered by name/dimension.</summary>
    public List<Product> GetProductsByCategory(string category)
    {
        var results = new List<Product>();
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, Category, Name, Dimension, Unit, Barcode, Price, Stock
            FROM Products
            WHERE Category = $cat
            ORDER BY Name, Dimension;";
        cmd.Parameters.AddWithValue("$cat", category);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadProduct(reader));

        return results;
    }

    /// <summary>
    /// Reads every row of one of the <see cref="BackupTables"/> as raw column
    /// values, for snapshotting to the remote backup. The table name is validated
    /// against the whitelist to avoid SQL injection.
    /// </summary>
    public (List<string> Columns, List<object?[]> Rows) ExportTable(string table)
    {
        if (Array.IndexOf(BackupTables, table) < 0)
            throw new ArgumentException($"Unknown backup table '{table}'.", nameof(table));

        var columns = new List<string>();
        var rows = new List<object?[]>();

        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {table};";

        using var reader = cmd.ExecuteReader();
        for (var i = 0; i < reader.FieldCount; i++)
            columns.Add(reader.GetName(i));

        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return (columns, rows);
    }

    public Product? GetProductById(long id)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT Id, Category, Name, Dimension, Unit, Barcode, Price, Stock FROM Products WHERE Id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", id);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadProduct(reader) : null;
    }

    /// <summary>Finds a single product by exact barcode/SKU match, or null if none.</summary>
    public Product? GetProductByBarcode(string barcode)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT Id, Category, Name, Dimension, Unit, Barcode, Price, Stock FROM Products WHERE Barcode = $barcode LIMIT 1;";
        cmd.Parameters.AddWithValue("$barcode", barcode.Trim());

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadProduct(reader) : null;
    }

    // ==================== Inventory writes (CRUD) ====================

    /// <summary>Inserts a new product and returns its new Id.</summary>
    public long AddProduct(Product product)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Products (Category, Name, Dimension, Unit, Barcode, Price, Stock)
            VALUES ($cat, $name, $dim, $unit, $sku, $price, $stock);
            SELECT last_insert_rowid();";
        BindProduct(cmd, product);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>Updates an existing product identified by <see cref="Product.Id"/>.</summary>
    public void UpdateProduct(Product product)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE Products
            SET Category = $cat, Name = $name, Dimension = $dim, Unit = $unit,
                Barcode = $sku, Price = $price, Stock = $stock
            WHERE Id = $id;";
        BindProduct(cmd, product);
        cmd.Parameters.AddWithValue("$id", product.Id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes a product by Id.</summary>
    public void DeleteProduct(long id)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Products WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static void BindProduct(SqliteCommand cmd, Product product)
    {
        cmd.Parameters.AddWithValue("$cat", product.Category ?? string.Empty);
        cmd.Parameters.AddWithValue("$name", product.Name ?? string.Empty);
        cmd.Parameters.AddWithValue("$dim", product.Dimension ?? string.Empty);
        cmd.Parameters.AddWithValue("$unit", string.IsNullOrWhiteSpace(product.Unit) ? "ea" : product.Unit);
        cmd.Parameters.AddWithValue("$sku", (object?)product.Barcode ?? string.Empty);
        cmd.Parameters.AddWithValue("$price", product.Price);
        cmd.Parameters.AddWithValue("$stock", product.Stock);
    }

    // ==================== Sales ====================

    /// <summary>
    /// Persists a sale and its items in a single transaction and decrements
    /// product stock. Returns the new Sale Id.
    /// </summary>
    public long RecordSale(Sale sale)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        // Allocated inside the transaction so the daily sequence stays unique.
        var receiptNo = NextReceiptNo(connection, transaction, sale.Timestamp);

        long saleId;
        using (var saleCmd = connection.CreateCommand())
        {
            saleCmd.Transaction = transaction;
            saleCmd.CommandText = @"
                INSERT INTO Sales
                    (ReceiptNo, Timestamp, CustomerName, CustomerPhone, CustomerAddress, Note,
                     Subtotal, Discount, TaxRate, TaxAmount, TotalAmount, AmountPaid, ChangeDue,
                     PaymentMethod)
                VALUES
                    ($no, $ts, $cname, $cphone, $caddr, $note,
                     $subtotal, $discount, $taxRate, $tax, $total, $paid, $change,
                     $method);
                SELECT last_insert_rowid();";
            saleCmd.Parameters.AddWithValue("$no", receiptNo);
            saleCmd.Parameters.AddWithValue("$ts", sale.Timestamp);
            saleCmd.Parameters.AddWithValue("$cname", sale.CustomerName ?? string.Empty);
            saleCmd.Parameters.AddWithValue("$cphone", sale.CustomerPhone ?? string.Empty);
            saleCmd.Parameters.AddWithValue("$caddr", sale.CustomerAddress ?? string.Empty);
            saleCmd.Parameters.AddWithValue("$note", sale.Note ?? string.Empty);
            saleCmd.Parameters.AddWithValue("$subtotal", sale.Subtotal);
            saleCmd.Parameters.AddWithValue("$discount", sale.Discount);
            saleCmd.Parameters.AddWithValue("$taxRate", sale.TaxRate);
            saleCmd.Parameters.AddWithValue("$tax", sale.TaxAmount);
            saleCmd.Parameters.AddWithValue("$total", sale.TotalAmount);
            saleCmd.Parameters.AddWithValue("$paid", sale.AmountPaid);
            saleCmd.Parameters.AddWithValue("$change", sale.ChangeDue);
            saleCmd.Parameters.AddWithValue("$method", sale.PaymentMethod);
            saleId = Convert.ToInt64(saleCmd.ExecuteScalar());
        }

        foreach (var item in sale.Items)
        {
            using (var itemCmd = connection.CreateCommand())
            {
                itemCmd.Transaction = transaction;
                itemCmd.CommandText = @"
                    INSERT INTO SaleItems
                        (SaleId, ProductId, Material, Dimension, Unit, Quantity, UnitPrice, LineTotal)
                    VALUES
                        ($saleId, $productId, $material, $dimension, $unit, $qty, $price, $lineTotal);";
                itemCmd.Parameters.AddWithValue("$saleId", saleId);
                itemCmd.Parameters.AddWithValue("$productId", item.ProductId);
                itemCmd.Parameters.AddWithValue("$material", item.Material ?? string.Empty);
                itemCmd.Parameters.AddWithValue("$dimension", item.Dimension ?? string.Empty);
                itemCmd.Parameters.AddWithValue("$unit", string.IsNullOrWhiteSpace(item.Unit) ? "ea" : item.Unit);
                itemCmd.Parameters.AddWithValue("$qty", item.Quantity);
                itemCmd.Parameters.AddWithValue("$price", item.UnitPrice);
                itemCmd.Parameters.AddWithValue("$lineTotal", item.LineTotal);
                itemCmd.ExecuteNonQuery();
            }

            using (var stockCmd = connection.CreateCommand())
            {
                stockCmd.Transaction = transaction;
                stockCmd.CommandText = @"
                    UPDATE Products
                    SET Stock = MAX(0, Stock - $qty)
                    WHERE Id = $productId;";
                stockCmd.Parameters.AddWithValue("$qty", item.Quantity);
                stockCmd.Parameters.AddWithValue("$productId", item.ProductId);
                stockCmd.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        sale.Id = saleId;
        sale.ReceiptNo = receiptNo;
        return saleId;
    }

    /// <summary>
    /// Loads one sale with all of its line items, for viewing the order detail and
    /// reprinting the original receipt.
    /// </summary>
    public Sale? GetSaleById(long saleId)
    {
        using var connection = OpenConnection();

        Sale? sale = null;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = SaleSelectSql + " WHERE s.Id = $id GROUP BY s.Id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", saleId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
                sale = ReadSale(reader);
        }

        if (sale is null)
            return null;

        sale.Items = GetSaleItems(connection, saleId);
        return sale;
    }

    /// <summary>Loads one sale by its receipt number.</summary>
    public Sale? GetSaleByReceiptNo(string receiptNo)
    {
        if (string.IsNullOrWhiteSpace(receiptNo))
            return null;

        using var connection = OpenConnection();

        Sale? sale = null;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = SaleSelectSql + " WHERE s.ReceiptNo = $no GROUP BY s.Id LIMIT 1;";
            cmd.Parameters.AddWithValue("$no", receiptNo.Trim());
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
                sale = ReadSale(reader);
        }

        if (sale is null)
            return null;

        sale.Items = GetSaleItems(connection, sale.Id);
        return sale;
    }

    private static List<SaleItem> GetSaleItems(SqliteConnection connection, long saleId)
    {
        var items = new List<SaleItem>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, SaleId, ProductId, Material, Dimension, Unit, Quantity, UnitPrice, LineTotal
            FROM SaleItems
            WHERE SaleId = $id
            ORDER BY Id;";
        cmd.Parameters.AddWithValue("$id", saleId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new SaleItem
            {
                Id = reader.GetInt64(0),
                SaleId = reader.GetInt64(1),
                ProductId = reader.GetInt64(2),
                Material = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                Dimension = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                Unit = reader.IsDBNull(5) ? "ea" : reader.GetString(5),
                Quantity = reader.GetInt32(6),
                UnitPrice = reader.GetDouble(7),
                LineTotal = reader.IsDBNull(8) ? 0 : reader.GetDouble(8),
            });
        }
        return items;
    }

    private const string SaleSelectSql = @"
        SELECT s.Id, s.ReceiptNo, s.Timestamp, s.CustomerName, s.CustomerPhone, s.CustomerAddress,
               s.Note, s.Subtotal, s.Discount, s.TaxRate, s.TaxAmount, s.TotalAmount,
               s.AmountPaid, s.ChangeDue, s.PaymentMethod,
               COALESCE(SUM(si.Quantity), 0) AS ItemCount
        FROM Sales s
        LEFT JOIN SaleItems si ON si.SaleId = s.Id";

    private static Sale ReadSale(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        ReceiptNo = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
        Timestamp = reader.GetDateTime(2),
        CustomerName = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
        CustomerPhone = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
        CustomerAddress = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
        Note = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
        Subtotal = reader.IsDBNull(7) ? 0 : reader.GetDouble(7),
        Discount = reader.IsDBNull(8) ? 0 : reader.GetDouble(8),
        TaxRate = reader.IsDBNull(9) ? 0 : reader.GetDouble(9),
        TaxAmount = reader.IsDBNull(10) ? 0 : reader.GetDouble(10),
        TotalAmount = reader.GetDouble(11),
        AmountPaid = reader.IsDBNull(12) ? 0 : reader.GetDouble(12),
        ChangeDue = reader.IsDBNull(13) ? 0 : reader.GetDouble(13),
        PaymentMethod = reader.IsDBNull(14) ? "Cash" : reader.GetString(14),
        ItemCount = reader.GetInt32(15),
    };

    /// <summary>
    /// Returns recent sales (newest first) for the Orders view. An optional search
    /// term matches the receipt number or the customer name.
    /// </summary>
    public List<Sale> GetRecentSales(int limit = 200, string? searchTerm = null)
    {
        var results = new List<Sale>();
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();

        var filter = string.IsNullOrWhiteSpace(searchTerm)
            ? string.Empty
            : " WHERE s.ReceiptNo LIKE $term OR s.CustomerName LIKE $term";

        cmd.CommandText = SaleSelectSql + filter + @"
            GROUP BY s.Id
            ORDER BY s.ReceiptNo DESC, s.Id DESC
            LIMIT $limit;";
        if (filter.Length > 0)
            cmd.Parameters.AddWithValue("$term", "%" + searchTerm!.Trim() + "%");
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadSale(reader));

        return results;
    }

    private static Product ReadProduct(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Category = reader.GetString(1),
        Name = reader.GetString(2),
        Dimension = reader.GetString(3),
        Unit = reader.GetString(4),
        Barcode = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
        Price = reader.GetDouble(6),
        Stock = reader.GetInt32(7),
    };
}
