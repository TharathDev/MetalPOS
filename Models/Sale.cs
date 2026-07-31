using System;
using System.Collections.Generic;

namespace PosApp.Models;

/// <summary>
/// A completed transaction. Everything needed to reprint the original receipt is
/// persisted here (customer, note, money breakdown) and in <see cref="Items"/>,
/// so a sale can be re-rendered exactly even after products change or are deleted.
/// </summary>
public class Sale
{
    public long Id { get; set; }

    /// <summary>
    /// Human-facing receipt number in the form yyyyMMdd + a 3-digit daily
    /// sequence, e.g. 20260731001. Sorts chronologically as text.
    /// </summary>
    public string ReceiptNo { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; } = DateTime.Now;

    // ----- Customer snapshot (receipt only, no customer table) -----
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerPhone { get; set; } = string.Empty;
    public string CustomerAddress { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;

    // ----- Money breakdown -----
    public double Subtotal { get; set; }
    public double Discount { get; set; }
    public double TaxRate { get; set; }
    public double TaxAmount { get; set; }
    public double TotalAmount { get; set; }
    public double AmountPaid { get; set; }
    public double ChangeDue { get; set; }
    public string PaymentMethod { get; set; } = "Cash";

    public List<SaleItem> Items { get; set; } = new();

    /// <summary>Total number of units sold (populated by history queries).</summary>
    public int ItemCount { get; set; }

    // ----- Display helpers for the Orders / history view -----
    public string ReceiptNoDisplay => string.IsNullOrWhiteSpace(ReceiptNo)
        ? $"#{Id:0000}"
        : ReceiptNo;
    public string TimestampDisplay => Timestamp.ToString("MMM d, yyyy  h:mm tt");
    public string DateDisplay => Timestamp.ToString("dd-MM-yyyy");
    public string TimeDisplay => Timestamp.ToString("h:mm tt");
    public string TotalDisplay => $"${TotalAmount:0.00}";
    public string SubtotalDisplay => $"${Subtotal:0.00}";
    public string DiscountDisplay => $"-${Discount:0.00}";
    public string TaxDisplay => TaxRate > 0 ? $"${TaxAmount:0.00} ({TaxRate:0.##}%)" : $"${TaxAmount:0.00}";
    public string AmountPaidDisplay => $"${AmountPaid:0.00}";
    public string ChangeDueDisplay => $"${ChangeDue:0.00}";
    public string ItemCountDisplay => ItemCount == 1 ? "1 item" : $"{ItemCount} items";
    public bool HasDiscount => Discount > 0;
    public bool HasTax => TaxAmount > 0;
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    public string CustomerDisplay => string.IsNullOrWhiteSpace(CustomerName)
        ? "Walk-in Customer"
        : CustomerName;

    public string ContactDisplay
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(CustomerPhone)) parts.Add(CustomerPhone);
            if (!string.IsNullOrWhiteSpace(CustomerAddress)) parts.Add(CustomerAddress);
            return string.Join("  ·  ", parts);
        }
    }

    public bool HasContact => !string.IsNullOrWhiteSpace(CustomerPhone)
                              || !string.IsNullOrWhiteSpace(CustomerAddress);
}

/// <summary>
/// A single line belonging to a <see cref="Sale"/>. The description fields are
/// copied from the product at the time of sale so history stays accurate even if
/// the product is later edited or removed.
/// </summary>
public class SaleItem
{
    public long Id { get; set; }
    public long SaleId { get; set; }
    public long ProductId { get; set; }
    public string Material { get; set; } = string.Empty;
    public string Dimension { get; set; } = string.Empty;
    public string Unit { get; set; } = "ea";
    public int Quantity { get; set; }
    public double UnitPrice { get; set; }
    public double LineTotal { get; set; }

    public string DimensionDisplay => string.IsNullOrWhiteSpace(Dimension) ? Unit : Dimension;
    public string UnitPriceDisplay => $"${UnitPrice:0.00}";
    public string LineTotalDisplay => $"${LineTotal:0.00}";
}
