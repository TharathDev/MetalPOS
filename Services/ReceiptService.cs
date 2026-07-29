using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using PosApp.Models;

namespace PosApp.Services;

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
    public string GenerateAndPrint(
        long saleId,
        DateTime timestamp,
        IReadOnlyList<CartLine> lines,
        double total,
        string paymentMethod,
        double amountPaid)
    {
        Directory.CreateDirectory(ReceiptsDirectory);

        var change = Math.Max(0, amountPaid - total);
        var html = BuildHtml(saleId, timestamp, lines, total, paymentMethod, amountPaid, change);

        var fileName = $"Receipt-{saleId:0000}-{timestamp:yyyyMMdd-HHmmss}.html";
        var path = Path.Combine(ReceiptsDirectory, fileName);
        File.WriteAllText(path, html, Encoding.UTF8);

        TryOpen(path);
        return path;
    }

    private static string BuildHtml(
        long saleId,
        DateTime timestamp,
        IReadOnlyList<CartLine> lines,
        double total,
        string paymentMethod,
        double amountPaid,
        double change)
    {
        var rows = new StringBuilder();
        foreach (var line in lines)
        {
            var name = System.Net.WebUtility.HtmlEncode(line.Material);
            var dim = System.Net.WebUtility.HtmlEncode(line.DimensionDisplay);
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

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"" />
<title>Receipt {saleId:0000}</title>
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
  .summary .row {{ display: flex; justify-content: space-between; padding: 3px 0; }}
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
      Sale {saleId:0000}<br />
      {timestamp:dddd, MMMM d, yyyy}<br />
      {timestamp:h:mm tt}
    </div>
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
      <div class=""row""><span>Subtotal</span><span>${total:0.00}</span></div>
      <div class=""row grand""><span>TOTAL</span><span class=""accent"">${total:0.00}</span></div>
      <div class=""row""><span>Payment ({System.Net.WebUtility.HtmlEncode(paymentMethod)})</span><span>${amountPaid:0.00}</span></div>
      <div class=""row""><span>Change</span><span>${change:0.00}</span></div>
    </div>
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

    private static void TryOpen(string path)
    {
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
