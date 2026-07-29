using CommunityToolkit.Mvvm.ComponentModel;

namespace PosApp.Models;

/// <summary>
/// A line in the current sale's cart. Both <see cref="Quantity"/> and
/// <see cref="UnitPrice"/> are editable at checkout so the cashier can adjust
/// the amount and the price per unit. Observable so the line total and the
/// cart total update live.
/// </summary>
public partial class CartLine : ObservableObject
{
    /// <summary>The source product id, used to decrement stock on checkout.</summary>
    public long ProductId { get; init; }

    public string Material { get; init; } = string.Empty;
    public string Dimension { get; init; } = string.Empty;
    public string Unit { get; init; } = "ea";

    /// <summary>Units available in stock, used to cap quantity increments.</summary>
    public int AvailableStock { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineTotal))]
    [NotifyPropertyChangedFor(nameof(LineTotalLabel))]
    public partial int Quantity { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineTotal))]
    [NotifyPropertyChangedFor(nameof(LineTotalLabel))]
    [NotifyPropertyChangedFor(nameof(UnitPriceLabel))]
    public partial double UnitPrice { get; set; }

    public double LineTotal => UnitPrice * Quantity;
    public string LineTotalLabel => $"${LineTotal:0.00}";
    public string UnitPriceLabel => $"${UnitPrice:0.00} ea";
    public string DimensionDisplay => string.IsNullOrWhiteSpace(Dimension) ? Unit : Dimension;
}
