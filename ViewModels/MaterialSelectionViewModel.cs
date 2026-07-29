using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PosApp.Models;

namespace PosApp.ViewModels;

/// <summary>
/// Drives the METALS_POS "Material Selection" dashboard and the "Select Dimensions"
/// modal drawer. UI-only for now: the dimension catalog is sample data and the cart
/// lives in memory. Everything is structured to be wired to the backend later.
/// </summary>
public partial class MaterialSelectionViewModel : ViewModelBase
{
    // ----- Dashboard state -----
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ActiveSection { get; set; } = "Inventory";

    [ObservableProperty]
    public partial string SyncLabel { get; set; } = "SYNC: 2M AGO";

    [ObservableProperty]
    public partial string DetailSubtitle { get; set; } = "Select an item to view details";

    // ----- Modal ("Select Dimensions") state -----
    [ObservableProperty]
    public partial bool IsDetailOpen { get; set; }

    /// <summary>Grade/subtitle shown under the modal title, e.g. "Alloy Steel Grade A36".</summary>
    [ObservableProperty]
    public partial string DetailMaterialName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DocSheetLabel { get; set; } = "Material Safety Data Sheet (PDF)";

    public ObservableCollection<DimensionOption> Dimensions { get; } = new();

    [ObservableProperty]
    public partial DimensionOption? SelectedDimension { get; set; }

    /// <summary>Active modal tab: "Selection" or "Cart".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectionTab))]
    [NotifyPropertyChangedFor(nameof(IsCartTab))]
    public partial string SelectedTab { get; set; } = "Selection";

    public bool IsSelectionTab => SelectedTab == "Selection";
    public bool IsCartTab => SelectedTab == "Cart";

    // ----- Cart state -----
    public ObservableCollection<CartLine> Cart { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CartTotalLabel))]
    [NotifyCanExecuteChangedFor(nameof(CheckoutCommand))]
    public partial double CartTotal { get; set; }

    [ObservableProperty]
    public partial string CartTabLabel { get; set; } = "Cart";

    [ObservableProperty]
    public partial bool IsCartEmpty { get; set; } = true;

    public string CartTotalLabel => $"${CartTotal:0.00}";

    // ==================== Commands ====================

    [RelayCommand]
    private void SelectSection(string? section)
    {
        if (!string.IsNullOrWhiteSpace(section))
            ActiveSection = section!;
    }

    /// <summary>Opens the modal for a category and loads its dimension options.</summary>
    [RelayCommand]
    private void SelectCategory(string? categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName))
            return;

        var (subtitle, docLabel, dims) = BuildCatalog(categoryName!);
        DetailMaterialName = subtitle;
        DocSheetLabel = docLabel;

        Dimensions.Clear();
        foreach (var d in dims)
            Dimensions.Add(d);

        SelectedDimension = Dimensions.FirstOrDefault();
        SelectedTab = "Selection";
        IsDetailOpen = true;
    }

    [RelayCommand]
    private void CloseDetail() => IsDetailOpen = false;

    [RelayCommand]
    private void SetTab(string? tab)
    {
        if (!string.IsNullOrWhiteSpace(tab))
            SelectedTab = tab!;
    }

    /// <summary>Adds a specific dimension row to the cart (per-row "ADD TO CART").</summary>
    [RelayCommand]
    private void AddDimensionToCart(DimensionOption? dimension)
    {
        if (dimension is null)
            return;

        var existing = Cart.FirstOrDefault(c =>
            c.Material == DetailMaterialName && c.Dimension == dimension.Dimension);

        if (existing is not null)
        {
            existing.Quantity++;
        }
        else
        {
            var line = new CartLine
            {
                Material = DetailMaterialName,
                Dimension = dimension.Dimension,
                UnitPrice = dimension.Price,
                Quantity = 1,
            };
            line.PropertyChanged += (_, _) => RecalculateCart();
            Cart.Add(line);
        }

        RecalculateCart();
    }

    /// <summary>Bottom "Add to Cart" button: adds the currently selected dimension.</summary>
    [RelayCommand]
    private void AddSelectedToCart()
    {
        var dimension = SelectedDimension ?? Dimensions.FirstOrDefault();
        if (dimension is null)
            return;

        AddDimensionToCart(dimension);
        SelectedTab = "Cart";
    }

    [RelayCommand]
    private void IncrementLine(CartLine? line)
    {
        if (line is not null)
            line.Quantity++;
    }

    [RelayCommand]
    private void DecrementLine(CartLine? line)
    {
        if (line is null)
            return;
        line.Quantity--;
        if (line.Quantity <= 0)
            Cart.Remove(line);
        RecalculateCart();
    }

    [RelayCommand]
    private void RemoveLine(CartLine? line)
    {
        if (line is null)
            return;
        Cart.Remove(line);
        RecalculateCart();
    }

    private bool HasCartItems() => Cart.Count > 0;

    [RelayCommand(CanExecute = nameof(HasCartItems))]
    private void Checkout()
    {
        var count = Cart.Sum(c => c.Quantity);
        var total = CartTotal;
        Cart.Clear();
        RecalculateCart();
        SelectedTab = "Selection";
        IsDetailOpen = false;
        DetailSubtitle = $"Order placed: {count} item(s), {total:C}. (Backend pending.)";
    }

    [RelayCommand]
    private void NewSale() => DetailSubtitle = "New sale started - pick a material category";

    /// <summary>Dashboard right-panel "ADD TO CART" (no item selected yet).</summary>
    [RelayCommand]
    private void AddToCart() => DetailSubtitle = "Pick a material category first, then choose a dimension.";

    [RelayCommand]
    private void AddCustomCategory() => DetailSubtitle = "Add a custom category (coming soon)";

    [RelayCommand]
    private void OpenHistory() => DetailSubtitle = "Recent history (coming soon)";

    [RelayCommand]
    private void OpenShipments() => DetailSubtitle = "Incoming shipments (coming soon)";

    [RelayCommand]
    private void OpenDocument(string? name) => DetailSubtitle = $"Opening {name} (coming soon)";

    private void RecalculateCart()
    {
        CartTotal = Cart.Sum(c => c.LineTotal);
        var count = Cart.Sum(c => c.Quantity);
        CartTabLabel = count > 0 ? $"Cart ({count})" : "Cart";
        IsCartEmpty = Cart.Count == 0;
        CheckoutCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Sample per-category dimension catalog. Replace with DB queries later.</summary>
    private static (string subtitle, string docLabel, DimensionOption[] dims) BuildCatalog(string category) =>
        category switch
        {
            "Steel" => ("Alloy Steel Grade A36", "A36 Material Safety Data Sheet (PDF)", new[]
            {
                new DimensionOption { Dimension = "2\" x 4\"",  Stock = 48, Price = 124.50 },
                new DimensionOption { Dimension = "4\" x 8\"",  Stock = 12, Price = 286.00 },
                new DimensionOption { Dimension = "12\" x 24\"", Stock = 5,  Price = 890.25 },
            }),
            "Iron" => ("Cast Iron Grade 65-45-12", "Cast Iron Data Sheet (PDF)", new[]
            {
                new DimensionOption { Dimension = "1\" Pipe (per ft)",   Stock = 120, Price = 18.75 },
                new DimensionOption { Dimension = "2\" Pipe (per ft)",   Stock = 64,  Price = 32.40 },
                new DimensionOption { Dimension = "Ornamental Casting",  Stock = 9,   Price = 145.00 },
            }),
            "Roofing" => ("Galvanized Corrugated G90", "G90 Coating Data Sheet (PDF)", new[]
            {
                new DimensionOption { Dimension = "26ga Sheet 3' x 8'",  Stock = 210, Price = 42.90 },
                new DimensionOption { Dimension = "Zinc Sheet 4' x 10'", Stock = 3,   Price = 96.50 },
                new DimensionOption { Dimension = "Ridge Cap (per ft)",  Stock = 88,  Price = 7.25 },
            }),
            "Tools" => ("Industrial Power Tools", "Tool Warranty & Safety (PDF)", new[]
            {
                new DimensionOption { Dimension = "Angle Grinder 4.5\"", Stock = 34, Price = 79.99 },
                new DimensionOption { Dimension = "MIG Welder 180A",     Stock = 6,  Price = 549.00 },
                new DimensionOption { Dimension = "Plasma Cutter 40A",   Stock = 4,  Price = 720.00 },
            }),
            "Hardware" => ("Fasteners & Fittings", "Fastener Spec Sheet (PDF)", new[]
            {
                new DimensionOption { Dimension = "1/2\" Hex Bolt (box)",  Stock = 500, Price = 24.00 },
                new DimensionOption { Dimension = "3/8\" Anchor (box)",    Stock = 320, Price = 31.50 },
                new DimensionOption { Dimension = "Heavy Hinge (pair)",    Stock = 76,  Price = 18.90 },
            }),
            _ => ($"{category} - General Stock", "Material Data Sheet (PDF)", new[]
            {
                new DimensionOption { Dimension = "Standard", Stock = 25, Price = 49.99 },
            }),
        };
}
