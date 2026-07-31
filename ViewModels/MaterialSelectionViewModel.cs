using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PosApp.Models;
using PosApp.Services;

namespace PosApp.ViewModels;

/// <summary>
/// Drives the METALS_POS window: the Inventory dashboard, the Stock management
/// (insert/update/delete) screen, the Orders history, and the "Select Dimensions"
/// modal with a live, editable cart + checkout that records the sale and prints a
/// receipt. All data is backed by the local SQLite database.
/// </summary>
public partial class MaterialSelectionViewModel : ViewModelBase
{
    private readonly DatabaseService _db;
    private readonly ReceiptService _receipt = new();
    private readonly TursoSyncService? _sync;

    public MaterialSelectionViewModel() : this(new DatabaseService()) { }

    public MaterialSelectionViewModel(DatabaseService db, TursoSyncService? sync = null)
    {
        _db = db;
        _sync = sync;
        PaymentMethods = new ObservableCollection<string> { "Cash", "Card", "Bank Transfer" };
        Units = new ObservableCollection<string> { "ea", "sheet", "ft", "box", "pair", "kg", "roll" };

        if (_sync is not null)
        {
            SyncLabel = _sync.Enabled ? "BACKUP: PENDING" : "BACKUP: OFF";
            _sync.StatusChanged += OnSyncStatusChanged;
        }

        SafeLoadAll();
    }

    private void OnSyncStatusChanged(SyncStatus status)
    {
        // StatusChanged fires on a background thread; marshal to the UI thread.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SyncLabel = status switch
            {
                { Enabled: false } => "BACKUP: OFF",
                { Success: true } => $"BACKED UP {status.Time:HH:mm}",
                _ => "BACKUP FAILED",
            };
            BackupStatus = status.Message;
        });
    }

    /// <summary>Triggers an immediate backup to the remote server.</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task BackupNow()
    {
        if (_sync is null)
            return;
        SyncLabel = "BACKING UP...";
        await _sync.SyncNowAsync().ConfigureAwait(false);
    }

    private void SafeLoadAll()
    {
        try
        {
            LoadCategories();
            LoadStock();
            LoadOrders();
        }
        catch
        {
            // Design-time / uninitialized DB: keep the previewer alive.
        }
    }

    // ==================== Sections ====================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInventorySection))]
    [NotifyPropertyChangedFor(nameof(IsStockSection))]
    [NotifyPropertyChangedFor(nameof(IsOrdersSection))]
    [NotifyPropertyChangedFor(nameof(IsCheckoutSection))]
    [NotifyPropertyChangedFor(nameof(IsTechnicalPanelVisible))]
    [NotifyPropertyChangedFor(nameof(SectionTitle))]
    [NotifyPropertyChangedFor(nameof(SectionSubtitle))]
    public partial string ActiveSection { get; set; } = "Inventory";

    public bool IsInventorySection => ActiveSection == "Inventory";
    public bool IsStockSection => ActiveSection == "Stock";
    public bool IsOrdersSection => ActiveSection is "Orders" or "Reports";
    public bool IsCheckoutSection => ActiveSection == "Checkout";

    [ObservableProperty]
    public partial int CategoryColumns { get; set; } = 3;

    [ObservableProperty]
    public partial double CategoryCardWidth { get; set; } = 240;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTechnicalPanelVisible))]
    public partial bool IsWideLayout { get; set; }

    /// <summary>The technical sidebar is useful only on a wide Inventory layout.</summary>
    public bool IsTechnicalPanelVisible => IsInventorySection && IsWideLayout;

    /// <summary>Called by the window when its width changes.</summary>
    public void UpdateResponsiveLayout(double width)
    {
        IsWideLayout = width >= 1440;
        CategoryColumns = IsWideLayout ? 4 : 3;

        // 224 sidebar + optional 300 specs + 40 effective content inset.
        // Subtract the 16px per-card margin after dividing into equal cells.
        var specsWidth = IsWideLayout ? 300d : 0d;
        var gridWidth = Math.Max(720d, width - 224d - specsWidth - 40d);
        CategoryCardWidth = Math.Floor(gridWidth / CategoryColumns) - 16d;

        foreach (var category in Categories)
            category.CardWidth = CategoryCardWidth;

        // Cart drawer occupies 3/10 of the window, with a sane minimum.
        CartDrawerWidth = Math.Max(320d, Math.Floor(width * 0.3d));
    }

    public string SectionTitle => ActiveSection switch
    {
        "Stock" => "Stock Management",
        "Orders" or "Reports" => "Orders & History",
        "Checkout" => "Checkout",
        _ => "Material Selection",
    };

    public string SectionSubtitle => ActiveSection switch
    {
        "Stock" => "Insert, update, or delete inventory items and create custom metal objects.",
        "Orders" or "Reports" => "Review completed sales and reprint receipts.",
        "Checkout" => "Verify quantities and pricing, add customer details, then complete the sale.",
        _ => "Select a category to view specific stock dimensions and pricing.",
    };

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    partial void OnSearchTextChanged(string value) => LoadStock();

    [ObservableProperty]
    public partial string SyncLabel { get; set; } = "SYNC: LIVE";

    [ObservableProperty]
    public partial string BackupStatus { get; set; } = "Cloud backup: waiting for first run.";

    [ObservableProperty]
    public partial string DetailSubtitle { get; set; } = "Select an item to view details";

    // ==================== Inventory dashboard ====================

    public ObservableCollection<CategoryInfo> Categories { get; } = new();

    private void LoadCategories()
    {
        Categories.Clear();
        foreach (var c in _db.GetCategories())
        {
            c.CardWidth = CategoryCardWidth;
            Categories.Add(c);
        }

        // Keep the custom-object action inside the same responsive grid so every
        // tile aligns to the same columns and row height.
        Categories.Add(new CategoryInfo
        {
            Name = "Add Custom Object",
            Description = "Create a new category, material, dimension, and stock item.",
            IsCustom = true,
            CardWidth = CategoryCardWidth,
        });
    }

    [RelayCommand]
    private void SelectSection(string? section)
    {
        if (string.IsNullOrWhiteSpace(section))
            return;
        ActiveSection = section!;
        if (IsOrdersSection)
            LoadOrders();
        if (IsStockSection)
            LoadStock();
        if (IsInventorySection)
            LoadCategories();
    }

    // ==================== "Select Dimensions" modal ====================

    [ObservableProperty]
    public partial bool IsDetailOpen { get; set; }

    [ObservableProperty]
    public partial string DetailCategory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DetailMaterialName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DocSheetLabel { get; set; } = "Material Data Sheet (PDF)";

    public ObservableCollection<Product> CategoryProducts { get; } = new();

    [ObservableProperty]
    public partial Product? SelectedProduct { get; set; }

    [RelayCommand]
    private void SelectCategory(string? categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName))
            return;

        DetailCategory = categoryName!;
        DocSheetLabel = $"{categoryName} Material Data Sheet (PDF)";

        CategoryProducts.Clear();
        foreach (var p in _db.GetProductsByCategory(categoryName!))
        {
            p.CartQuantity = Cart.FirstOrDefault(line => line.ProductId == p.Id)?.Quantity ?? 0;
            CategoryProducts.Add(p);
        }

        var materials = CategoryProducts.Select(p => p.Name).Distinct().ToList();
        DetailMaterialName = materials.Count switch
        {
            0 => "No stock in this category yet",
            1 => materials[0],
            _ => $"{materials.Count} materials · {CategoryProducts.Count} SKUs",
        };

        SelectedProduct = CategoryProducts.FirstOrDefault();
        IsDetailOpen = true;
    }

    [RelayCommand]
    private void CloseDetail() => IsDetailOpen = false;

    // ==================== Cart ====================

    public ObservableCollection<CartLine> Cart { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CartTotalLabel))]
    [NotifyPropertyChangedFor(nameof(SubtotalLabel))]
    [NotifyPropertyChangedFor(nameof(DiscountAmount))]
    [NotifyPropertyChangedFor(nameof(DiscountLabel))]
    [NotifyPropertyChangedFor(nameof(TaxAmount))]
    [NotifyPropertyChangedFor(nameof(TaxLabel))]
    [NotifyPropertyChangedFor(nameof(GrandTotal))]
    [NotifyPropertyChangedFor(nameof(GrandTotalLabel))]
    [NotifyPropertyChangedFor(nameof(ChangeLabel))]
    [NotifyCanExecuteChangedFor(nameof(GoToCheckoutCommand))]
    [NotifyCanExecuteChangedFor(nameof(CompleteSaleCommand))]
    public partial double CartTotal { get; set; }

    [ObservableProperty]
    public partial bool IsCartEmpty { get; set; } = true;

    /// <summary>Total units in the cart, shown as the sidebar cart badge.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCartBadge))]
    public partial int CartItemCount { get; set; }

    public bool HasCartBadge => CartItemCount > 0;

    /// <summary>Controls the left slide-in cart drawer.</summary>
    [ObservableProperty]
    public partial bool IsCartDrawerOpen { get; set; }

    /// <summary>Cart drawer spans 3/10 of the window width.</summary>
    [ObservableProperty]
    public partial double CartDrawerWidth { get; set; } = 360;

    [RelayCommand]
    private void ToggleCartDrawer() => IsCartDrawerOpen = !IsCartDrawerOpen;

    [RelayCommand]
    private void CloseCartDrawer() => IsCartDrawerOpen = false;

    public string CartTotalLabel => $"${CartTotal:0.00}";

    public ObservableCollection<string> PaymentMethods { get; }

    [ObservableProperty]
    public partial string PaymentMethod { get; set; } = "Cash";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChangeLabel))]
    public partial string AmountPaidText { get; set; } = string.Empty;

    // ==================== Checkout screen ====================

    // Customer details are printed on the receipt only - never stored in the database.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomerSummary))]
    public partial string CustomerName { get; set; } = string.Empty;
    [ObservableProperty] public partial string CustomerPhone { get; set; } = string.Empty;
    [ObservableProperty] public partial string CustomerAddress { get; set; } = string.Empty;

    /// <summary>Free-text note printed on the receipt (e.g. cut-to-size instructions).</summary>
    [ObservableProperty] public partial string OrderNote { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiscountAmount))]
    [NotifyPropertyChangedFor(nameof(DiscountLabel))]
    [NotifyPropertyChangedFor(nameof(TaxAmount))]
    [NotifyPropertyChangedFor(nameof(TaxLabel))]
    [NotifyPropertyChangedFor(nameof(GrandTotal))]
    [NotifyPropertyChangedFor(nameof(GrandTotalLabel))]
    [NotifyPropertyChangedFor(nameof(ChangeLabel))]
    public partial string DiscountText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaxAmount))]
    [NotifyPropertyChangedFor(nameof(TaxLabel))]
    [NotifyPropertyChangedFor(nameof(GrandTotal))]
    [NotifyPropertyChangedFor(nameof(GrandTotalLabel))]
    [NotifyPropertyChangedFor(nameof(ChangeLabel))]
    public partial string TaxRateText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CheckoutStatus { get; set; } = string.Empty;

    /// <summary>Sum of all cart lines before discount and tax.</summary>
    public double Subtotal => CartTotal;
    public string SubtotalLabel => $"${Subtotal:0.00}";

    /// <summary>Flat discount amount, clamped to the subtotal.</summary>
    public double DiscountAmount => Math.Clamp(ParseDouble(DiscountText, 0), 0, Subtotal);
    public string DiscountLabel => $"-${DiscountAmount:0.00}";

    public double TaxRate => Math.Max(0, ParseDouble(TaxRateText, 0));
    public double TaxAmount => (Subtotal - DiscountAmount) * TaxRate / 100d;
    public string TaxLabel => $"${TaxAmount:0.00}";

    /// <summary>Final payable amount: subtotal - discount + tax.</summary>
    public double GrandTotal => Math.Max(0, Subtotal - DiscountAmount + TaxAmount);
    public string GrandTotalLabel => $"${GrandTotal:0.00}";

    public string ChangeLabel
    {
        get
        {
            var paid = ParseDouble(AmountPaidText, GrandTotal);
            var change = paid - GrandTotal;
            return change < 0 ? $"Due ${-change:0.00}" : $"Change ${change:0.00}";
        }
    }

    /// <summary>Customer summary line for the receipt; falls back to a walk-in label.</summary>
    public string CustomerSummary =>
        string.IsNullOrWhiteSpace(CustomerName) ? "Walk-in Customer" : CustomerName.Trim();

    /// <summary>Navigates to the focused checkout screen without completing the sale.</summary>
    [RelayCommand(CanExecute = nameof(HasCartItems))]
    private void GoToCheckout()
    {
        if (Cart.Count == 0)
            return;
        IsCartDrawerOpen = false;
        IsDetailOpen = false;
        ActiveSection = "Checkout";
        CheckoutStatus = string.Empty;
    }

    /// <summary>Returns from checkout to browsing inventory.</summary>
    [RelayCommand]
    private void BackToShopping()
    {
        ActiveSection = "Inventory";
        LoadCategories();
    }

    [RelayCommand]
    private void AddProductToCart(Product? product)
    {
        if (product is null || product.Stock <= 0)
            return;

        var existing = Cart.FirstOrDefault(c => c.ProductId == product.Id);
        if (existing is not null)
        {
            if (existing.Quantity >= existing.AvailableStock)
                return;
            existing.Quantity++;
            SyncProductCartQuantity(product.Id, existing.Quantity);
        }
        else
        {
            var line = new CartLine
            {
                ProductId = product.Id,
                Material = string.IsNullOrWhiteSpace(product.Name) ? product.Category : product.Name,
                Dimension = product.Dimension,
                Unit = product.Unit,
                AvailableStock = product.Stock,
                UnitPrice = product.Price,
                Quantity = 1,
            };
            line.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(CartLine.Quantity))
                    SyncProductCartQuantity(line.ProductId, Math.Max(0, line.Quantity));
                RecalculateCart();
            };
            Cart.Add(line);
            SyncProductCartQuantity(product.Id, 1);
        }

        RecalculateCart();
    }

    [RelayCommand]
    private void IncrementProductQuantity(Product? product) => AddProductToCart(product);

    [RelayCommand]
    private void DecrementProductQuantity(Product? product)
    {
        if (product is null)
            return;

        var line = Cart.FirstOrDefault(c => c.ProductId == product.Id);
        if (line is null)
        {
            SyncProductCartQuantity(product.Id, 0);
            return;
        }

        DecrementLine(line);
    }

    [RelayCommand]
    private void AddSelectedToCart()
    {
        var product = SelectedProduct ?? CategoryProducts.FirstOrDefault();
        if (product is null)
            return;
        AddProductToCart(product);

        // The cart now lives in the left drawer, so surface it there.
        IsDetailOpen = false;
        IsCartDrawerOpen = true;
    }

    [RelayCommand]
    private void IncrementLine(CartLine? line)
    {
        if (line is null || line.Quantity >= line.AvailableStock)
            return;
        line.Quantity++;
        SyncProductCartQuantity(line.ProductId, line.Quantity);
        RecalculateCart();
    }

    [RelayCommand]
    private void DecrementLine(CartLine? line)
    {
        if (line is null)
            return;
        line.Quantity--;
        if (line.Quantity <= 0)
        {
            Cart.Remove(line);
            SyncProductCartQuantity(line.ProductId, 0);
        }
        else
        {
            SyncProductCartQuantity(line.ProductId, line.Quantity);
        }
        RecalculateCart();
    }

    [RelayCommand]
    private void RemoveLine(CartLine? line)
    {
        if (line is null)
            return;
        Cart.Remove(line);
        SyncProductCartQuantity(line.ProductId, 0);
        RecalculateCart();
    }

    private void SyncProductCartQuantity(long productId, int quantity)
    {
        var product = CategoryProducts.FirstOrDefault(p => p.Id == productId);
        if (product is not null)
            product.CartQuantity = Math.Max(0, quantity);
    }

    private bool HasCartItems() => Cart.Count > 0;

    /// <summary>
    /// Finalizes the sale from the checkout screen: records it, decrements stock,
    /// prints the receipt (including the non-persisted customer details), then
    /// resets the cart and checkout fields.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasCartItems))]
    private void CompleteSale()
    {
        if (Cart.Count == 0)
            return;

        var paid = ParseDouble(AmountPaidText, GrandTotal);
        if (paid + 0.0001 < GrandTotal)
        {
            CheckoutStatus = $"Amount paid is short by ${GrandTotal - paid:0.00}.";
            return;
        }

        var timestamp = DateTime.Now;
        var lines = Cart.ToList();
        var total = GrandTotal;

        var sale = new Sale
        {
            Timestamp = timestamp,
            TotalAmount = total,
            PaymentMethod = PaymentMethod,
            Items = lines.Select(l => new SaleItem
            {
                ProductId = l.ProductId,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
            }).ToList(),
        };

        long saleId = _db.RecordSale(sale);

        try
        {
            _receipt.GenerateAndPrint(new ReceiptRequest
            {
                SaleId = saleId,
                Timestamp = timestamp,
                Lines = lines,
                Subtotal = Subtotal,
                Discount = DiscountAmount,
                TaxRate = TaxRate,
                Tax = TaxAmount,
                Total = total,
                PaymentMethod = PaymentMethod,
                AmountPaid = paid,
                CustomerName = CustomerSummary,
                CustomerPhone = CustomerPhone?.Trim() ?? string.Empty,
                CustomerAddress = CustomerAddress?.Trim() ?? string.Empty,
                Note = OrderNote?.Trim() ?? string.Empty,
            });
        }
        catch
        {
            // Receipt generation is best-effort and must not fail the sale.
        }

        var itemCount = lines.Sum(l => l.Quantity);

        foreach (var product in CategoryProducts)
            product.CartQuantity = 0;
        Cart.Clear();
        ResetCheckoutFields();
        RecalculateCart();

        // Refresh stock-derived views.
        LoadCategories();
        LoadStock();
        LoadOrders();

        // Refresh the open category modal, if any.
        if (IsDetailOpen && !string.IsNullOrWhiteSpace(DetailCategory))
        {
            CategoryProducts.Clear();
            foreach (var p in _db.GetProductsByCategory(DetailCategory))
                CategoryProducts.Add(p);
        }

        IsDetailOpen = false;
        IsCartDrawerOpen = false;
        ActiveSection = "Orders";
        DetailSubtitle = $"Sale #{saleId:0000} complete: {itemCount} item(s), ${total:0.00}. Receipt printed.";
    }

    private void ResetCheckoutFields()
    {
        AmountPaidText = string.Empty;
        DiscountText = string.Empty;
        TaxRateText = string.Empty;
        CustomerName = string.Empty;
        CustomerPhone = string.Empty;
        CustomerAddress = string.Empty;
        OrderNote = string.Empty;
        CheckoutStatus = string.Empty;
    }

    private void RecalculateCart()
    {
        CartTotal = Cart.Sum(c => c.LineTotal);
        var count = Cart.Sum(c => c.Quantity);
        CartItemCount = count;
        IsCartEmpty = Cart.Count == 0;
        OnPropertyChanged(nameof(Subtotal));
        OnPropertyChanged(nameof(SubtotalLabel));
        OnPropertyChanged(nameof(DiscountAmount));
        OnPropertyChanged(nameof(DiscountLabel));
        OnPropertyChanged(nameof(TaxAmount));
        OnPropertyChanged(nameof(TaxLabel));
        OnPropertyChanged(nameof(GrandTotal));
        OnPropertyChanged(nameof(GrandTotalLabel));
        OnPropertyChanged(nameof(ChangeLabel));
        GoToCheckoutCommand.NotifyCanExecuteChanged();
        CompleteSaleCommand.NotifyCanExecuteChanged();
    }

    // ==================== Stock management (CRUD) ====================

    public ObservableCollection<Product> StockItems { get; } = new();
    public ObservableCollection<string> Units { get; }

    [ObservableProperty]
    public partial string StockStatus { get; set; } = "Ready.";

    /// <summary>Controls the slide-in add/edit form drawer on the Stock screen.</summary>
    [ObservableProperty]
    public partial bool IsStockFormOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    [NotifyPropertyChangedFor(nameof(FormTitle))]
    [NotifyPropertyChangedFor(nameof(SaveButtonLabel))]
    public partial long EditingProductId { get; set; }

    public bool IsEditing => EditingProductId != 0;
    public string FormTitle => IsEditing ? "Edit Item" : "Add New Item";
    public string SaveButtonLabel => IsEditing ? "Update Item" : "Add Item";

    [ObservableProperty] public partial string FormCategory { get; set; } = string.Empty;
    [ObservableProperty] public partial string FormName { get; set; } = string.Empty;
    [ObservableProperty] public partial string FormDimension { get; set; } = string.Empty;
    [ObservableProperty] public partial string FormUnit { get; set; } = "ea";
    [ObservableProperty] public partial string FormSku { get; set; } = string.Empty;
    [ObservableProperty] public partial string FormPriceText { get; set; } = string.Empty;
    [ObservableProperty] public partial string FormStockText { get; set; } = string.Empty;

    private void LoadStock()
    {
        StockItems.Clear();
        foreach (var p in _db.SearchProducts(SearchText))
            StockItems.Add(p);
    }

    /// <summary>Resets the form fields (used by the drawer's "Clear" button).</summary>
    [RelayCommand]
    private void NewProduct()
    {
        EditingProductId = 0;
        FormCategory = string.Empty;
        FormName = string.Empty;
        FormDimension = string.Empty;
        FormUnit = "ea";
        FormSku = string.Empty;
        FormPriceText = string.Empty;
        FormStockText = string.Empty;
        StockStatus = "Enter details for a new metal object.";
    }

    /// <summary>Opens the drawer with a blank form to create a new metal object.</summary>
    [RelayCommand]
    private void OpenAddProduct()
    {
        NewProduct();
        IsStockFormOpen = true;
    }

    /// <summary>Closes the add/edit drawer without saving.</summary>
    [RelayCommand]
    private void CloseStockForm() => IsStockFormOpen = false;

    [RelayCommand]
    private void EditProductRow(Product? product)
    {
        if (product is null)
            return;
        EditingProductId = product.Id;
        FormCategory = product.Category;
        FormName = product.Name;
        FormDimension = product.Dimension;
        FormUnit = string.IsNullOrWhiteSpace(product.Unit) ? "ea" : product.Unit;
        FormSku = product.Barcode;
        FormPriceText = product.Price.ToString("0.00", CultureInfo.CurrentCulture);
        FormStockText = product.Stock.ToString(CultureInfo.CurrentCulture);
        StockStatus = $"Editing \"{product.Name} {product.Dimension}\".";
        IsStockFormOpen = true;
    }

    [RelayCommand]
    private void SaveProduct()
    {
        if (string.IsNullOrWhiteSpace(FormCategory))
        {
            StockStatus = "Category is required.";
            return;
        }
        if (string.IsNullOrWhiteSpace(FormName))
        {
            StockStatus = "Material name is required.";
            return;
        }
        if (!TryParseDouble(FormPriceText, out var price) || price < 0)
        {
            StockStatus = "Enter a valid, non-negative price.";
            return;
        }
        if (!TryParseInt(FormStockText, out var stock) || stock < 0)
        {
            StockStatus = "Enter a valid, non-negative stock quantity.";
            return;
        }

        var product = new Product
        {
            Id = EditingProductId,
            Category = FormCategory.Trim(),
            Name = FormName.Trim(),
            Dimension = FormDimension.Trim(),
            Unit = string.IsNullOrWhiteSpace(FormUnit) ? "ea" : FormUnit.Trim(),
            Barcode = FormSku.Trim(),
            Price = price,
            Stock = stock,
        };

        string message;
        if (EditingProductId == 0)
        {
            var id = _db.AddProduct(product);
            message = $"Added \"{product.Name} {product.Dimension}\" (#{id}).";
        }
        else
        {
            _db.UpdateProduct(product);
            message = $"Updated \"{product.Name} {product.Dimension}\".";
        }

        NewProduct();
        IsStockFormOpen = false;
        LoadStock();
        LoadCategories();
        StockStatus = message;
    }

    [RelayCommand]
    private void DeleteProductRow(Product? product)
    {
        if (product is null)
            return;
        _db.DeleteProduct(product.Id);
        if (EditingProductId == product.Id)
        {
            NewProduct();
            IsStockFormOpen = false;
        }
        StockStatus = $"Deleted \"{product.Name} {product.Dimension}\".";
        LoadStock();
        LoadCategories();
    }

    /// <summary>Sidebar "Add Custom Category": jump to Stock with a blank form.</summary>
    [RelayCommand]
    private void AddCustomCategory()
    {
        ActiveSection = "Stock";
        NewProduct();
        IsStockFormOpen = true;
        StockStatus = "Create a custom metal object: set a new category name and details.";
    }

    // ==================== Orders / history ====================

    public ObservableCollection<Sale> RecentSales { get; } = new();

    [ObservableProperty]
    public partial string OrdersSummary { get; set; } = "No sales yet.";

    private void LoadOrders()
    {
        RecentSales.Clear();
        var sales = _db.GetRecentSales();
        foreach (var s in sales)
            RecentSales.Add(s);

        var total = sales.Sum(s => s.TotalAmount);
        OrdersSummary = sales.Count == 0
            ? "No sales yet."
            : $"{sales.Count} sale(s) · ${total:0.00} total revenue";
    }

    // ==================== Misc commands ====================

    [RelayCommand]
    private void NewSale()
    {
        foreach (var product in CategoryProducts)
            product.CartQuantity = 0;
        Cart.Clear();
        ResetCheckoutFields();
        RecalculateCart();
        IsCartDrawerOpen = false;
        ActiveSection = "Inventory";
        DetailSubtitle = "New sale started - pick a material category.";
    }

    [RelayCommand]
    private void AddToCart() => DetailSubtitle = "Pick a material category first, then choose a dimension.";

    [RelayCommand]
    private void OpenHistory()
    {
        ActiveSection = "Orders";
        LoadOrders();
    }

    [RelayCommand]
    private void OpenShipments() => DetailSubtitle = "Incoming shipments (coming soon)";

    [RelayCommand]
    private void OpenDocument(string? name) => DetailSubtitle = $"Opening {name} (coming soon)";

    // ==================== Parsing helpers ====================

    private static bool TryParseDouble(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out value) ||
        double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value);

    private static bool TryParseInt(string? text, out int value) =>
        int.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out value) ||
        int.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value);

    private static double ParseDouble(string? text, double fallback) =>
        TryParseDouble(text, out var value) ? value : fallback;
}
