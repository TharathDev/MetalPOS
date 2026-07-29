using CommunityToolkit.Mvvm.ComponentModel;

namespace PosApp.Models;

/// <summary>
/// A line in the in-memory cart used by the "Select Dimensions" modal.
/// Observable so quantity edits update the line total and cart total live.
/// </summary>
public partial class CartLine : ObservableObject
{
    public string Material { get; init; } = string.Empty;
    public string Dimension { get; init; } = string.Empty;
    public double UnitPrice { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineTotal))]
    [NotifyPropertyChangedFor(nameof(LineTotalLabel))]
    public partial int Quantity { get; set; }

    public double LineTotal => UnitPrice * Quantity;
    public string LineTotalLabel => $"${LineTotal:0.00}";
    public string UnitPriceLabel => $"${UnitPrice:0.00} ea";
}
