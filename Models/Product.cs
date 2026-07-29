namespace PosApp.Models;

/// <summary>
/// A sellable metal stock item. A product belongs to a <see cref="Category"/>
/// (e.g. "Steel"), has a material/grade <see cref="Name"/> (e.g. "Alloy Steel
/// Grade A36") and a specific purchasable <see cref="Dimension"/> (e.g. 2" x 4").
/// </summary>
public class Product
{
    public long Id { get; set; }

    /// <summary>Top-level category, e.g. Steel, Iron, Roofing, Tools, Hardware, or a custom one.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Material / grade name, e.g. "Alloy Steel Grade A36".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Specific dimension / spec, e.g. "2\" x 4\"" or "1\" Pipe (per ft)".</summary>
    public string Dimension { get; set; } = string.Empty;

    /// <summary>Unit of sale, e.g. sheet, ft, box, pair, ea.</summary>
    public string Unit { get; set; } = "ea";

    /// <summary>SKU / barcode identifier.</summary>
    public string Barcode { get; set; } = string.Empty;

    public double Price { get; set; }

    public int Stock { get; set; }

    // ----- Display helpers used by the inventory / stock views -----

    public string MaterialLine => string.IsNullOrWhiteSpace(Name) ? Category : Name;
    public string DimensionLabel => string.IsNullOrWhiteSpace(Dimension)
        ? "Standard"
        : $"Dimension: {Dimension}";
    public string StockLabel => $"Stock: {Stock} {Unit} available";
    public string PriceLabel => $"${Price:0.00}";
    public string StockDisplay => $"{Stock} {Unit}";
    public string SkuDisplay => string.IsNullOrWhiteSpace(Barcode) ? "-" : Barcode;
    public string CategoryDimensionLine => string.IsNullOrWhiteSpace(Dimension)
        ? Category
        : $"{Category}  ·  {Dimension}";

    /// <summary>True when stock is low (used to highlight rows in the stock table).</summary>
    public bool IsLowStock => Stock <= 5;

    public string Display => $"{Name} {Dimension}  ${Price:0.00}  ({Stock} {Unit})";
}
