namespace PosApp.Models;

/// <summary>
/// A specific purchasable dimension/spec of a material category, shown as a row
/// in the "Select Dimensions" modal (e.g. 2" x 4", 48 in stock, $124.50).
/// </summary>
public class DimensionOption
{
    public string Dimension { get; init; } = string.Empty;
    public int Stock { get; init; }
    public double Price { get; init; }

    public string DimensionLabel => $"Dimension: {Dimension}";
    public string StockLabel => $"Stock: {Stock} units available";
    public string PriceLabel => $"${Price:0.00}";
}
