using System;

namespace PosApp.Models;

/// <summary>
/// A shop / business profile. Its details print on every receipt and invoice.
/// The <see cref="Id"/> is a stable GUID, <see cref="MachineId"/> ties the shop
/// to the device it was created on, and <see cref="RecoveryKey"/> (e.g. the
/// owner's phone number) lets the shop be found again after a device change.
/// The design supports more than one shop for the future.
/// </summary>
public class Shop
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    // ----- Shop profile (printed on receipts) -----
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    /// <summary>VAT / TIN registration number shown on the receipt.</summary>
    public string VatTin { get; set; } = string.Empty;

    /// <summary>Default VAT percent applied at checkout.</summary>
    public double VatRate { get; set; }

    /// <summary>Free-text line printed at the bottom of the receipt.</summary>
    public string ReceiptFooter { get; set; } = string.Empty;

    // ----- App settings -----
    /// <summary>Display currency: "USD" or "KHR" (Khmer Riel).</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>Khmer Riel per 1 USD, used to show a converted total.</summary>
    public double ExchangeRate { get; set; } = 4100;

    // ----- Identity / recovery -----
    public string MachineId { get; set; } = string.Empty;
    public string RecoveryKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    // ----- Helpers -----
    public bool IsKhmer => string.Equals(Currency, "KHR", StringComparison.OrdinalIgnoreCase);
    public string CurrencyCode => IsKhmer ? "KHR" : "USD";

    /// <summary>Converts a USD amount to the shop's display currency.</summary>
    public double ToDisplayCurrency(double usd) => IsKhmer ? usd * ExchangeRate : usd;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "My Shop" : Name;
}
