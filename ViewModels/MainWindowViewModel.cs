using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PosApp.Models;
using PosApp.Services;

namespace PosApp.ViewModels;

/// <summary>
/// Drives the main POS screen: product search/selection on the left and the
/// live cart + checkout on the right.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly DatabaseService _db;

    /// <summary>Products shown in the left panel (filtered by <see cref="SearchText"/>).</summary>
    public ObservableCollection<Product> Products { get; } = new();

    /// <summary>Items currently in the cart (right panel).</summary>
    public ObservableCollection<CartItem> Cart { get; } = new();

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalAmountDisplay))]
    [NotifyCanExecuteChangedFor(nameof(CheckoutCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearCartCommand))]
    public partial double TotalAmount { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Ready.";

    public string TotalAmountDisplay => $"${TotalAmount:0.00}";

    /// <summary>Parameterless ctor keeps the XAML previewer happy (design-time only).</summary>
    public MainWindowViewModel() : this(new DatabaseService())
    {
    }

    public MainWindowViewModel(DatabaseService db)
    {
        _db = db;
        LoadProducts();
    }

    // Re-run the search whenever the search text changes (live filtering / scanning).
    partial void OnSearchTextChanged(string value) => LoadProducts();

    private void LoadProducts()
    {
        Products.Clear();
        foreach (var product in _db.SearchProducts(SearchText))
            Products.Add(product);
    }

    /// <summary>
    /// Handles a barcode entry (typically from a scanner that ends input with Enter).
    /// If the search text is an exact barcode match, adds that product and clears the box.
    /// </summary>
    [RelayCommand]
    private void SubmitSearch()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
            return;

        var byBarcode = _db.GetProductByBarcode(SearchText);
        if (byBarcode is not null)
        {
            AddToCart(byBarcode);
            SearchText = string.Empty; // triggers LoadProducts()
            return;
        }

        // Otherwise, if exactly one product matches the text, add it for convenience.
        if (Products.Count == 1)
        {
            AddToCart(Products[0]);
            SearchText = string.Empty;
        }
    }

    /// <summary>Adds a product to the cart, incrementing quantity if already present.</summary>
    [RelayCommand]
    private void AddToCart(Product? product)
    {
        if (product is null)
            return;

        var existing = Cart.FirstOrDefault(c => c.ProductId == product.Id);
        if (existing is not null)
        {
            if (existing.Quantity >= existing.AvailableStock)
            {
                StatusMessage = $"Only {existing.AvailableStock} of {product.Name} in stock.";
                return;
            }
            existing.Quantity++;
        }
        else
        {
            if (product.Stock <= 0)
            {
                StatusMessage = $"{product.Name} is out of stock.";
                return;
            }
            var item = new CartItem
            {
                ProductId = product.Id,
                Name = product.Name,
                UnitPrice = product.Price,
                AvailableStock = product.Stock,
                Quantity = 1,
            };
            item.PropertyChanged += (_, _) => RecalculateTotal();
            Cart.Add(item);
        }

        StatusMessage = $"Added {product.Name}.";
        RecalculateTotal();
    }

    [RelayCommand]
    private void IncrementItem(CartItem? item)
    {
        if (item is null)
            return;
        if (item.Quantity >= item.AvailableStock)
        {
            StatusMessage = $"Only {item.AvailableStock} of {item.Name} in stock.";
            return;
        }
        item.Quantity++;
        RecalculateTotal();
    }

    [RelayCommand]
    private void DecrementItem(CartItem? item)
    {
        if (item is null)
            return;
        item.Quantity--;
        if (item.Quantity <= 0)
            Cart.Remove(item);
        RecalculateTotal();
    }

    [RelayCommand]
    private void RemoveItem(CartItem? item)
    {
        if (item is null)
            return;
        Cart.Remove(item);
        RecalculateTotal();
    }

    private bool HasItems() => Cart.Count > 0;

    [RelayCommand(CanExecute = nameof(HasItems))]
    private void ClearCart()
    {
        Cart.Clear();
        RecalculateTotal();
        StatusMessage = "Cart cleared.";
    }

    [RelayCommand(CanExecute = nameof(HasItems))]
    private void Checkout()
    {
        var sale = new Sale
        {
            Timestamp = DateTime.Now,
            TotalAmount = TotalAmount,
            PaymentMethod = "Cash",
            Items = Cart.Select(c => new SaleItem
            {
                ProductId = c.ProductId,
                Quantity = c.Quantity,
                UnitPrice = c.UnitPrice,
            }).ToList(),
        };

        var itemCount = Cart.Sum(c => c.Quantity);
        var total = TotalAmount;

        long saleId = _db.RecordSale(sale);

        Cart.Clear();
        RecalculateTotal();
        LoadProducts(); // refresh stock counts in the left panel
        StatusMessage = $"Sale #{saleId} complete: {itemCount} item(s), ${total:0.00}. Thank you!";
    }

    private void RecalculateTotal()
    {
        TotalAmount = Cart.Sum(c => c.LineTotal);
        // CanExecute for Checkout/ClearCart depends on Cart contents.
        CheckoutCommand.NotifyCanExecuteChanged();
        ClearCartCommand.NotifyCanExecuteChanged();
    }
}
