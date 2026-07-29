using System;
using System.Collections.Generic;

namespace PosApp.Models;

/// <summary>
/// A completed transaction persisted to the Sales table, together with its line items.
/// </summary>
public class Sale
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public double TotalAmount { get; set; }
    public string PaymentMethod { get; set; } = "Cash";
    public List<SaleItem> Items { get; set; } = new();

    /// <summary>Total number of units sold (populated by history queries).</summary>
    public int ItemCount { get; set; }

    // ----- Display helpers for the Orders / history view -----
    public string SaleNumber => $"#{Id:0000}";
    public string TimestampDisplay => Timestamp.ToString("MMM d, yyyy  h:mm tt");
    public string TotalDisplay => $"${TotalAmount:0.00}";
    public string ItemCountDisplay => ItemCount == 1 ? "1 item" : $"{ItemCount} items";
}

/// <summary>
/// A single product line belonging to a <see cref="Sale"/> (SaleItems table).
/// </summary>
public class SaleItem
{
    public long Id { get; set; }
    public long SaleId { get; set; }
    public long ProductId { get; set; }
    public int Quantity { get; set; }
    public double UnitPrice { get; set; }
}
