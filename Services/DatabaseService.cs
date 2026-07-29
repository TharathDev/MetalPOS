using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using PosApp.Models;

namespace PosApp.Services;

/// <summary>
/// Owns the local SQLite database (pos_data.db). Responsible for creating the
/// schema on first run, seeding sample products, and all read/write access.
/// </summary>
public class DatabaseService
{
    private readonly string _connectionString;

    public DatabaseService()
    {
        // Store the DB next to the running executable so it is self-contained.
        var dbPath = Path.Combine(AppContext.BaseDirectory, "pos_data.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath
        }.ToString();
    }

    /// <summary>Full path to the SQLite file, useful for logging/diagnostics.</summary>
    public string DatabasePath => AppContext.BaseDirectory + "pos_data.db";

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        // Enforce foreign keys for referential integrity between sales and items.
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
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Products (
                Id      INTEGER PRIMARY KEY AUTOINCREMENT,
                Name    TEXT    NOT NULL,
                Barcode TEXT,
                Price   REAL    NOT NULL DEFAULT 0,
                Stock   INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS Sales (
                Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp     DATETIME NOT NULL,
                TotalAmount   REAL     NOT NULL,
                PaymentMethod TEXT     NOT NULL
            );

            CREATE TABLE IF NOT EXISTS SaleItems (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                SaleId    INTEGER NOT NULL,
                ProductId INTEGER NOT NULL,
                Quantity  INTEGER NOT NULL,
                UnitPrice REAL    NOT NULL,
                FOREIGN KEY (SaleId)    REFERENCES Sales(Id),
                FOREIGN KEY (ProductId) REFERENCES Products(Id)
            );";
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

        var samples = new (string Name, string Barcode, double Price, int Stock)[]
        {
            ("Espresso",        "1000000000017", 2.50,  100),
            ("Cappuccino",      "1000000000024", 3.25,  80),
            ("Blueberry Muffin","1000000000031", 2.75,  40),
            ("Bottled Water",   "1000000000048", 1.50,  200),
            ("Chocolate Bar",   "1000000000055", 1.95,  120),
        };

        using var transaction = connection.BeginTransaction();
        foreach (var s in samples)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO Products (Name, Barcode, Price, Stock) VALUES ($name, $barcode, $price, $stock);";
            insert.Parameters.AddWithValue("$name", s.Name);
            insert.Parameters.AddWithValue("$barcode", s.Barcode);
            insert.Parameters.AddWithValue("$price", s.Price);
            insert.Parameters.AddWithValue("$stock", s.Stock);
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>
    /// Returns products matching the search term against name or barcode.
    /// An empty search term returns all products ordered by name.
    /// </summary>
    public List<Product> SearchProducts(string? searchTerm)
    {
        var results = new List<Product>();
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            cmd.CommandText = "SELECT Id, Name, Barcode, Price, Stock FROM Products ORDER BY Name;";
        }
        else
        {
            cmd.CommandText = @"
                SELECT Id, Name, Barcode, Price, Stock
                FROM Products
                WHERE Name LIKE $term OR Barcode LIKE $term
                ORDER BY Name;";
            cmd.Parameters.AddWithValue("$term", "%" + searchTerm.Trim() + "%");
        }

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadProduct(reader));

        return results;
    }

    /// <summary>Finds a single product by exact barcode match, or null if none.</summary>
    public Product? GetProductByBarcode(string barcode)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT Id, Name, Barcode, Price, Stock FROM Products WHERE Barcode = $barcode LIMIT 1;";
        cmd.Parameters.AddWithValue("$barcode", barcode.Trim());

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadProduct(reader) : null;
    }

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
                // Guard against negative stock at the SQL level.
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

    private static Product ReadProduct(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Name = reader.GetString(1),
        Barcode = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
        Price = reader.GetDouble(3),
        Stock = reader.GetInt32(4),
    };
}
