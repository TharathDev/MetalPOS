using CommunityToolkit.Mvvm.ComponentModel;

namespace PosApp.Models;

/// <summary>
/// A sellable metal stock item. A product belongs to a <see cref="Category"/>
/// (e.g. "Steel"), has a material/grade <see cref="Name"/> (e.g. "Alloy Steel
/// Grade A36") and a specific purchasable <see cref="Dimension"/> (e.g. 2" x 4").
/// </summary>
public partial class Product : ObservableObject
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

    private bool _isKhmer;
    public bool IsKhmer
    {
        get => _isKhmer;
        set
        {
            if (_isKhmer == value)
                return;
            _isKhmer = value;
            OnPropertyChanged(nameof(LocalizedUnit));
            OnPropertyChanged(nameof(LocalizedStockLabel));
            OnPropertyChanged(nameof(LocalizedStockDisplay));
        }
    }

    /// <summary>Quantity of this product currently present in the active cart.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInCart))]
    [NotifyPropertyChangedFor(nameof(CanIncrease))]
    public partial int CartQuantity { get; set; }

    public bool IsInCart => CartQuantity > 0;

    /// <summary>False once the cart quantity has consumed all available stock.</summary>
    public bool CanIncrease => CartQuantity < Stock;

    // ----- Display helpers used by the inventory / stock views -----

    public string MaterialLine => string.IsNullOrWhiteSpace(Name) ? Category : Name;
    public string DimensionLabel => string.IsNullOrWhiteSpace(Dimension)
        ? "Standard"
        : $"Dimension: {Dimension}";
    public string StockLabel => $"Stock: {Stock} {Unit} available";
    public string LocalizedUnit => IsKhmer ? Unit switch
    {
        "ea" => "ដុំ",
        "sheet" => "សន្លឹក",
        "ft" => "ហ្វីត",
        "in" => "អ៊ីញ",
        "box" => "ប្រអប់",
        "pair" => "គូ",
        "kg" => "គីឡូក្រាម",
        "roll" => "រមូរ",
        "mm" => "មីលីម៉ែត្រ",
        "cm" => "សង់ទីម៉ែត្រ",
        "dm" => "ដេស៊ីម៉ែត្រ",
        "m" => "ម៉ែត្រ",
        "cm²" => "សង់ទីម៉ែត្រការ៉េ",
        "dm²" => "ដេស៊ីម៉ែត្រការ៉េ",
        "m²" => "ម៉ែត្រការ៉េ",
        _ => Unit,
    } : Unit;
    public string LocalizedStockLabel => IsKhmer
        ? $"ស្តុក៖ {Stock} {LocalizedUnit} មាន"
        : StockLabel;
    public string PriceLabel => $"${Price:0.00}";
    public string StockDisplay => $"{Stock} {Unit}";
    public string LocalizedStockDisplay => IsKhmer ? $"{Stock} {LocalizedUnit}" : StockDisplay;
    public string SkuDisplay => string.IsNullOrWhiteSpace(Barcode) ? "-" : Barcode;
    public string CategoryDimensionLine => string.IsNullOrWhiteSpace(Dimension)
        ? Category
        : $"{Category}  ·  {Dimension}";

    /// <summary>True when stock is low (used to highlight rows in the stock table).</summary>
    public bool IsLowStock => Stock <= 5;

    public string Display => $"{Name} {Dimension}  ${Price:0.00}  ({Stock} {Unit})";
}
