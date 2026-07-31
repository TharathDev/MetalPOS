namespace PosApp.Models;

/// <summary>Aggregate totals for a reporting period.</summary>
public class SalesSummary
{
    public int OrderCount { get; init; }
    public int ItemsSold { get; init; }
    public double Revenue { get; init; }
    public double Discount { get; init; }
    public double Tax { get; init; }

    public double AverageOrder => OrderCount == 0 ? 0 : Revenue / OrderCount;

    public string OrderCountDisplay => OrderCount.ToString("N0");
    public string ItemsSoldDisplay => ItemsSold.ToString("N0");
    public string RevenueDisplay => $"${Revenue:N2}";
    public string DiscountDisplay => $"${Discount:N2}";
    public string TaxDisplay => $"${Tax:N2}";
    public string AverageOrderDisplay => $"${AverageOrder:N2}";
}

/// <summary>
/// One row of the "units sold per type" stock report: how much of a given
/// product/material was sold in the period, and the revenue it brought in.
/// </summary>
public class ProductSalesRow
{
    public string Category { get; init; } = string.Empty;
    public string Material { get; init; } = string.Empty;
    public string Dimension { get; init; } = string.Empty;
    public string Unit { get; init; } = "ea";
    public int QuantitySold { get; init; }
    public int OrderCount { get; init; }
    public double Revenue { get; init; }

    public string ProductName => string.IsNullOrWhiteSpace(Material) ? Category : Material;
    public string DimensionDisplay => string.IsNullOrWhiteSpace(Dimension) ? "-" : Dimension;
    public string QuantityDisplay => $"{QuantitySold:N0} {Unit}";
    public string RevenueDisplay => $"${Revenue:N2}";
    public string CategoryDisplay => string.IsNullOrWhiteSpace(Category) ? "-" : Category;
}

/// <summary>Units sold and revenue grouped by category, for the category summary.</summary>
public class CategorySalesRow
{
    public string Category { get; init; } = string.Empty;
    public int QuantitySold { get; init; }
    public double Revenue { get; init; }

    public string CategoryDisplay => string.IsNullOrWhiteSpace(Category) ? "Uncategorized" : Category;
    public string QuantityDisplay => QuantitySold.ToString("N0");
    public string RevenueDisplay => $"${Revenue:N2}";
}
