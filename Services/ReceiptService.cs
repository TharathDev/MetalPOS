using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using PosApp.Models;

namespace PosApp.Services;

/// <summary>
/// All values needed to render one receipt. Customer details are supplied per
/// sale and are never persisted to the database.
/// </summary>
public class ReceiptRequest
{
    public long SaleId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public IReadOnlyList<CartLine> Lines { get; init; } = Array.Empty<CartLine>();
    public double Subtotal { get; init; }
    public double Discount { get; init; }
    public double TaxRate { get; init; }
    public double Tax { get; init; }
    public double Total { get; init; }
    public string PaymentMethod { get; init; } = "Cash";
    public double AmountPaid { get; init; }
    public string CustomerName { get; init; } = "Walk-in Customer";
    public string CustomerPhone { get; init; } = string.Empty;
    public string CustomerAddress { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// Builds a printable receipt for a completed sale, writes it to disk as an
/// HTML document, and opens it with the OS default handler. The generated page
/// triggers the browser/system print dialog automatically so the cashier can
/// print to any installed printer.
/// </summary>
public class ReceiptService
{
    private const string StoreName = "METALS POS";
    private const string StoreTagline = "Industrial Metal Supply";

    /// <summary>Directory where receipts are saved (next to the executable).</summary>
    public string ReceiptsDirectory => Path.Combine(AppContext.BaseDirectory, "Receipts");

    /// <summary>
    /// Generates a receipt HTML file for the sale and opens it for printing.
    /// Returns the full path of the receipt file that was written.
    /// </summary>
    public string GenerateAndPrint(ReceiptRequest request)
    {
        Directory.CreateDirectory(ReceiptsDirectory);

        var html = BuildHtml(request);

        var fileName = $"Receipt-{request.SaleId:0000}-{request.Timestamp:yyyyMMdd-HHmmss}.html";
        var path = Path.Combine(ReceiptsDirectory, fileName);
        File.WriteAllText(path, html, Encoding.UTF8);

        TryOpen(path);
        return path;
    }

    private static string BuildHtml(ReceiptRequest r)
    {
        var change = Math.Max(0, r.AmountPaid - r.Total);

        var rows = new StringBuilder();
        foreach (var line in r.Lines)
        {
            var name = Encode(line.Material);
            var dim = Encode(line.DimensionDisplay);
            rows.Append($@"
      <tr>
        <td class=""item"">
          <div class=""name"">{name}</div>
          <div class=""dim"">{dim}</div>
        </td>
        <td class=""num"">{line.Quantity}</td>
        <td class=""num"">${line.UnitPrice:0.00}</td>
        <td class=""num total"">${line.LineTotal:0.00}</td>
      </tr>");
        }

        // Customer block: only render the lines that were actually filled in.
        var customer = new StringBuilder();
        customer.Append($@"<div class=""row""><span>Customer</span><span>{Encode(r.CustomerName)}</span></div>");
        if (!string.IsNullOrWhiteSpace(r.CustomerPhone))
            customer.Append($@"<div class=""row""><span>Phone</span><span>{Encode(r.CustomerPhone)}</span></div>");
        if (!string.IsNullOrWhiteSpace(r.CustomerAddress))
            customer.Append($@"<div class=""addr"">{Encode(r.CustomerAddress)}</div>");

        // Optional discount / tax rows.
        var adjustments = new StringBuilder();
        if (r.Discount > 0)
            adjustments.Append($@"<div class=""row""><span>Discount</span><span>-${r.Discount:0.00}</span></div>");
        if (r.Tax > 0)
            adjustments.Append($@"<div class=""row""><span>Tax ({r.TaxRate:0.##}%)</span><span>${r.Tax:0.00}</span></div>");

        var note = string.IsNullOrWhiteSpace(r.Note)
            ? string.Empty
            : $@"<hr /><div class=""note""><strong>Note:</strong> {Encode(r.Note)}</div>";

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"" />
<title>Receipt {r.SaleId:0000}</title>
<style>
  * {{ box-sizing: border-box; }}
  body {{ font-family: 'Courier New', Consolas, monospace; color: #1f2937; background: #f3f4f6; margin: 0; padding: 24px; }}
  .receipt {{ width: 320px; margin: 0 auto; background: #fff; padding: 22px 20px; border: 1px solid #e5e7eb; }}
  .center {{ text-align: center; }}
  .store {{ font-size: 20px; font-weight: bold; letter-spacing: 2px; }}
  .tagline {{ font-size: 11px; color: #6b7280; margin-top: 2px; }}
  .meta {{ font-size: 11px; color: #6b7280; margin-top: 12px; }}
  hr {{ border: none; border-top: 1px dashed #9ca3af; margin: 12px 0; }}
  table {{ width: 100%; border-collapse: collapse; font-size: 12px; }}
  th {{ text-align: left; color: #6b7280; font-weight: normal; border-bottom: 1px solid #e5e7eb; padding-bottom: 4px; }}
  th.num, td.num {{ text-align: right; }}
  td.item {{ padding: 6px 0; }}
  .name {{ font-weight: bold; }}
  .dim {{ color: #6b7280; font-size: 11px; }}
  td.total {{ font-weight: bold; }}
  .summary {{ font-size: 13px; margin-top: 6px; }}
  .summary .row, .cust .row {{ display: flex; justify-content: space-between; padding: 3px 0; }}
  .cust {{ font-size: 12px; }}
  .addr {{ font-size: 11px; color: #6b7280; padding-top: 2px; }}
  .note {{ font-size: 11px; color: #374151; }}
  .grand {{ font-size: 16px; font-weight: bold; border-top: 2px solid #1f2937; margin-top: 6px; padding-top: 8px; }}
  .accent {{ color: #c2410c; }}
  .footer {{ font-size: 11px; color: #6b7280; margin-top: 16px; }}
  @media print {{ body {{ background: #fff; padding: 0; }} .receipt {{ border: none; }} .noprint {{ display: none; }} }}
  .btn {{ display:block; width: 320px; margin: 14px auto 0; padding: 10px; background:#c2410c; color:#fff; border:none; font-size:14px; cursor:pointer; }}
</style>
</head>
<body onload=""setTimeout(function(){{ window.print(); }}, 250);"">
  <div class=""receipt"">
    <div class=""center"">
      <div class=""store accent"">{StoreName}</div>
      <div class=""tagline"">{StoreTagline}</div>
    </div>
    <div class=""meta center"">
      Sale {r.SaleId:0000}<br />
      {r.Timestamp:dddd, MMMM d, yyyy}<br />
      {r.Timestamp:h:mm tt}
    </div>
    <hr />
    <div class=""cust"">{customer}</div>
    <hr />
    <table>
      <thead>
        <tr><th>Item</th><th class=""num"">Qty</th><th class=""num"">Price</th><th class=""num"">Total</th></tr>
      </thead>
      <tbody>{rows}
      </tbody>
    </table>
    <hr />
    <div class=""summary"">
      <div class=""row""><span>Subtotal</span><span>${r.Subtotal:0.00}</span></div>
      {adjustments}
      <div class=""row grand""><span>TOTAL</span><span class=""accent"">${r.Total:0.00}</span></div>
      <div class=""row""><span>Payment ({Encode(r.PaymentMethod)})</span><span>${r.AmountPaid:0.00}</span></div>
      <div class=""row""><span>Change</span><span>${change:0.00}</span></div>
    </div>
    {note}
    <hr />
    <div class=""footer center"">
      Thank you for your business!<br />
      All sales final on cut-to-size orders.
    </div>
  </div>
  <button class=""btn noprint"" onclick=""window.print()"">Print Receipt</button>
</body>
</html>";
    }

    private static string Encode(string? value) =>
        System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    private static void TryOpen(string path)
    {
        // Allows generating receipts without launching a viewer (tests / headless runs).
        if (Environment.GetEnvironmentVariable("POS_SUPPRESS_RECEIPT_OPEN") == "1")
            return;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", $"\"{path}\"");
            }
            else
            {
                Process.Start("xdg-open", $"\"{path}\"");
            }
        }
        catch
        {
            // Opening the receipt is best-effort; the file is still written to disk.
        }
    }
}
