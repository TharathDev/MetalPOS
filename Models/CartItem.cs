using CommunityToolkit.Mvvm.ComponentModel;

namespace PosApp.Models;

/// <summary>
/// A line item in the current sale. Observable so the cart list and totals
/// update live when quantities change.
/// </summary>
public partial class CartItem : ObservableObject
{
    public long ProductId { get; init; }

    public string Name { get; init; } = string.Empty;

    public double UnitPrice { get; init; }

    /// <summary>How many units of this product are currently available in stock.</summary>
    public int AvailableStock { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineTotal))]
    public partial int Quantity { get; set; }

    public double LineTotal => UnitPrice * Quantity;
}
