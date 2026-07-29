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
