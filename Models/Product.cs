namespace PosApp.Models;

/// <summary>
/// Represents a sellable product stored in the Products table.
/// </summary>
public class Product
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Barcode { get; set; } = string.Empty;
    public double Price { get; set; }
    public int Stock { get; set; }

    // Convenience display string for list rows, e.g. "Espresso  $2.50  (24 in stock)".
    public string Display => $"{Name}   ${Price:0.00}   ({Stock} in stock)";
}
