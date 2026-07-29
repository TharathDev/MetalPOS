namespace PosApp.Models;

/// <summary>
/// Aggregated information about a product category, used to render the dynamic
/// category cards on the Inventory dashboard.
/// </summary>
public class CategoryInfo
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int SkuCount { get; init; }

    /// <summary>Total units of stock across all products in this category.</summary>
    public int TotalStock { get; init; }

    public string SkuLabel => SkuCount == 1 ? "1 SKU" : $"{SkuCount} SKUs";
    public string StockLine => TotalStock <= 5
        ? "Low stock - reorder soon"
        : $"{TotalStock} units in stock";
    public bool IsLowStock => TotalStock <= 5;
}
