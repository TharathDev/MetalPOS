using System;
using System.Collections.Generic;
using System.IO;
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
    /// The individual CREATE TABLE statements that define the schema. Shared by
    /// <see cref="Initialize"/> and the remote backup so the local and remote
    /// databases always use the exact same structure.
    /// </summary>
    public static readonly string[] SchemaStatements =
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
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            Timestamp     DATETIME NOT NULL,
            TotalAmount   REAL     NOT NULL,
            PaymentMethod TEXT     NOT NULL
        )",
        @"CREATE TABLE IF NOT EXISTS SaleItems (
            Id        INTEGER PRIMARY KEY AUTOINCREMENT,
            SaleId    INTEGER NOT NULL,
            ProductId INTEGER NOT NULL,
            Quantity  INTEGER NOT NULL,
            UnitPrice REAL    NOT NULL,
            FOREIGN KEY (SaleId)    REFERENCES Sales(Id),
            FOREIGN KEY (ProductId) REFERENCES Products(Id)
        )",
    };

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
        using var cmd = connection.CreateCommand();
        cmd.CommandText = string.Join(";\n", SchemaStatements) + ";";
        cmd.ExecuteNonQuery();

        SeedProductsIfEmpty(connection);
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

        long saleId;
        using (var saleCmd = connection.CreateCommand())
        {
            saleCmd.Transaction = transaction;
            saleCmd.CommandText = @"
                INSERT INTO Sales (Timestamp, TotalAmount, PaymentMethod)
                VALUES ($ts, $total, $method);
                SELECT last_insert_rowid();";
            saleCmd.Parameters.AddWithValue("$ts", sale.Timestamp);
            saleCmd.Parameters.AddWithValue("$total", sale.TotalAmount);
            saleCmd.Parameters.AddWithValue("$method", sale.PaymentMethod);
            saleId = Convert.ToInt64(saleCmd.ExecuteScalar());
        }

        foreach (var item in sale.Items)
        {
            using (var itemCmd = connection.CreateCommand())
            {
                itemCmd.Transaction = transaction;
                itemCmd.CommandText = @"
                    INSERT INTO SaleItems (SaleId, ProductId, Quantity, UnitPrice)
                    VALUES ($saleId, $productId, $qty, $price);";
                itemCmd.Parameters.AddWithValue("$saleId", saleId);
                itemCmd.Parameters.AddWithValue("$productId", item.ProductId);
                itemCmd.Parameters.AddWithValue("$qty", item.Quantity);
                itemCmd.Parameters.AddWithValue("$price", item.UnitPrice);
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
        return saleId;
    }

    /// <summary>Returns the most recent sales for the Orders view.</summary>
    public List<Sale> GetRecentSales(int limit = 50)
    {
        var results = new List<Sale>();
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT s.Id, s.Timestamp, s.TotalAmount, s.PaymentMethod,
                   COALESCE(SUM(si.Quantity), 0) AS ItemCount
            FROM Sales s
            LEFT JOIN SaleItems si ON si.SaleId = s.Id
            GROUP BY s.Id
            ORDER BY s.Timestamp DESC
            LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new Sale
            {
                Id = reader.GetInt64(0),
                Timestamp = reader.GetDateTime(1),
                TotalAmount = reader.GetDouble(2),
                PaymentMethod = reader.GetString(3),
                ItemCount = reader.GetInt32(4),
            });
        }
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
