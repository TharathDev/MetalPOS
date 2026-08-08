using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using PosApp.Models;

namespace PosApp.Services;

/// <summary>
/// All values needed to render one receipt. Customer details are supplied per
/// sale and are never persisted to the database.
/// </summary>
/// <summary>One printable line on a receipt, independent of the cart.</summary>
public class ReceiptLine
{
    public string Description { get; init; } = string.Empty;
    public string Unit { get; init; } = "ea";
    public int Quantity { get; init; }
    public double UnitPrice { get; init; }
    public double LineTotal { get; init; }
}

public class ReceiptRequest
{
    public long SaleId { get; init; }

    /// <summary>Receipt number in the form yyyyMMdd + 3-digit daily sequence.</summary>
    public string ReceiptNo { get; init; } = string.Empty;

    public DateTime Timestamp { get; init; } = DateTime.Now;
    public IReadOnlyList<ReceiptLine> Lines { get; init; } = Array.Empty<ReceiptLine>();
    public double Subtotal { get; init; }
    public double Discount { get; init; }
    public double TaxRate { get; init; }
    public double Tax { get; init; }
    public double Total { get; init; }
    public string PaymentMethod { get; init; } = "Cash";
    public double AmountPaid { get; init; }
    public string CustomerName { get; init; } = "អតិថិជនទូទៅ";
    public string CustomerPhone { get; init; } = string.Empty;
    public string CustomerAddress { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
    public string InvoiceFormat { get; init; } = "Detailed";

    // ----- Optional sales-confirmation header fields -----
    public string QuotationNo { get; init; } = string.Empty;
    public string ModeOfDelivery { get; init; } = string.Empty;
    public string DeliveryAddress { get; init; } = string.Empty;
    public string RequestedShipDate { get; init; } = string.Empty;
    public string DeliveryContact { get; init; } = string.Empty;

    /// <summary>Builds a request from a stored sale, so any past order reprints identically.</summary>
    public static ReceiptRequest FromSale(Sale sale) => new()
    {
        SaleId = sale.Id,
        ReceiptNo = sale.ReceiptNo,
        Timestamp = sale.Timestamp,
        Lines = sale.Items.Select(i => new ReceiptLine
        {
            Description = string.IsNullOrWhiteSpace(i.Dimension)
                ? i.Material
                : $"{i.Material} - {i.Dimension}",
            Unit = i.Unit,
            Quantity = i.Quantity,
            UnitPrice = i.UnitPrice,
            LineTotal = i.LineTotal,
        }).ToList(),
        Subtotal = sale.Subtotal,
        Discount = sale.Discount,
        TaxRate = sale.TaxRate,
        Tax = sale.TaxAmount,
        Total = sale.TotalAmount,
        PaymentMethod = sale.PaymentMethod,
        AmountPaid = sale.AmountPaid,
        CustomerName = string.IsNullOrWhiteSpace(sale.CustomerName) ? "អតិថិជនទូទៅ" : sale.CustomerName,
        CustomerPhone = sale.CustomerPhone,
        CustomerAddress = sale.CustomerAddress,
        DeliveryAddress = sale.CustomerAddress,
        DeliveryContact = sale.CustomerPhone,
        Note = sale.Note,
        InvoiceFormat = sale.InvoiceFormat,
    };
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

        var html = request.InvoiceFormat == "WalkIn"
            ? BuildWalkInInvoiceHtml(request)
            : BuildHtml(request);

        var number = string.IsNullOrWhiteSpace(request.ReceiptNo)
            ? request.SaleId.ToString("0000", CultureInfo.InvariantCulture)
            : request.ReceiptNo;
        var fileName = $"Receipt-{number}.html";
        var path = Path.Combine(ReceiptsDirectory, fileName);
        File.WriteAllText(path, html, Encoding.UTF8);

        TryOpen(path);
        return path;
    }

    /// <summary>Re-opens an already generated receipt file for another print.</summary>
    public void OpenExisting(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            TryOpen(path);
    }

    /// <summary>
    /// Renders the Khmer sales-confirmation document (ប័ណ្ណបញ្ជាក់ការលក់).
    /// Labels are trilingual (Khmer / Chinese / English) to match the printed
    /// form used in the shop; the primary language is Khmer.
    /// </summary>
    private static string BuildHtml(ReceiptRequest r)
    {
        var inv = CultureInfo.InvariantCulture;
        var change = Math.Max(0, r.AmountPaid - r.Total);
        var receiptNo = string.IsNullOrWhiteSpace(r.ReceiptNo)
            ? r.SaleId.ToString("0000000", inv)
            : r.ReceiptNo;

        var rows = new StringBuilder();
        var index = 0;
        foreach (var line in r.Lines)
        {
            index++;
            rows.Append($@"
        <tr>
          <td class=""c"">{index}</td>
          <td class=""desc"">{Encode(line.Description)}</td>
          <td class=""r"">{Encode(line.Unit)}</td>
          <td class=""r"">{line.Quantity.ToString("N2", inv)}</td>
          <td class=""r"">{line.UnitPrice.ToString("N3", inv)}</td>
          <td class=""r"">0.000</td>
          <td class=""r b"">{line.LineTotal.ToString("N2", inv)}</td>
        </tr>");
        }

        // Keep the table a consistent height on short sales, like the paper form.
        var filler = Math.Max(0, 8 - index);
        for (var i = 0; i < filler; i++)
        {
            rows.Append(@"
        <tr class=""blank"">
          <td class=""c"">&nbsp;</td><td></td><td></td><td></td><td></td><td></td><td></td>
        </tr>");
        }

        var taxRow = r.Tax > 0
            ? $@"<tr>
            <td class=""tl"">អាករ <span class=""sl"">/</span> <span class=""cn"">稅</span> <span class=""sl"">/</span> <span class=""en"">TAX ({r.TaxRate.ToString("0.##", inv)}%)</span></td>
            <td class=""tv"">{r.Tax.ToString("N2", inv)}</td>
          </tr>"
            : string.Empty;

        var noteBlock = string.IsNullOrWhiteSpace(r.Note)
            ? string.Empty
            : $@"<div class=""note""><b>សម្គាល់ <span class=""sl"">/</span> <span class=""cn"">備註</span> <span class=""sl"">/</span> <span class=""en"">NOTE</span> :</b> {Encode(r.Note)}</div>";

        return $@"<!DOCTYPE html>
<html lang=""km"">
<head>
<meta charset=""utf-8"" />
<title>ប័ណ្ណបញ្ជាក់ការលក់ {Encode(receiptNo)}</title>
<style>
  /* Khmer-first font stack using fonts shipped with macOS / Windows / Linux. */
  :root {{
    --khmer: 'Khmer OS Battambang', 'Khmer OS', 'Khmer MN', 'Khmer Sangam MN',
             'Noto Sans Khmer', 'Noto Serif Khmer', 'Khmer UI', 'Leelawadee UI', sans-serif;
  }}
  * {{ box-sizing: border-box; }}
  body {{ font-family: var(--khmer); color: #000; background: #f3f4f6; margin: 0; padding: 18px; font-size: 12px; }}
  .sheet {{ width: 760px; margin: 0 auto; background: #fff; padding: 16px 18px 22px; border: 1px solid #999; }}
  .cn {{ font-family: 'PingFang SC', 'Hiragino Sans GB', 'Microsoft YaHei', sans-serif; }}
  .en {{ font-family: Arial, Helvetica, sans-serif; }}
  /* Separator between the Khmer / Chinese / English parts of a label. */
  .sl {{ padding: 0 3px; }}

  /* ---- header ---- */
  .head {{ display: flex; align-items: flex-start; gap: 12px; }}
  .head .col {{ flex: 1; }}
  .head .mid {{ flex: 1.5; text-align: center; }}
  .title-km {{ font-size: 19px; font-weight: bold; line-height: 1.5; }}
  .title-cn {{ font-size: 13px; margin-top: 2px; }}
  .title-en {{ font-size: 13px; font-weight: bold; letter-spacing: .5px; margin-top: 2px; }}
  .kv {{ display: flex; gap: 6px; margin-bottom: 6px; font-size: 11px; }}
  .kv .k {{ white-space: nowrap; }}
  .kv .v {{ font-weight: bold; }}
  .right .kv {{ justify-content: flex-start; }}

  /* ---- customer block ---- */
  .info {{ margin-top: 14px; border-top: 1px solid #000; padding-top: 10px; }}
  .info table {{ width: 100%; border-collapse: collapse; }}
  .info td {{ padding: 3px 0; vertical-align: top; font-size: 11.5px; }}
  .info td.lbl {{ width: 265px; }}
  .info td.sep {{ width: 12px; }}
  .info td.val {{ font-weight: bold; }}

  /* ---- items ---- */
  table.items {{ width: 100%; border-collapse: collapse; margin-top: 12px; }}
  table.items th, table.items td {{ border: 1px solid #000; padding: 4px 5px; font-size: 11.5px; }}
  table.items th {{ text-align: center; font-weight: normal; line-height: 1.35; vertical-align: middle; }}
  table.items th .km {{ display: block; }}
  table.items th .cn {{ display: block; font-size: 10.5px; }}
  table.items th .en {{ display: block; font-size: 10.5px; font-weight: bold; }}
  td.c {{ text-align: center; }}
  td.r {{ text-align: right; }}
  td.b {{ font-weight: bold; }}
  td.desc {{ word-break: break-word; }}
  tr.blank td {{ height: 19px; }}

  /* ---- footer ---- */
  .foot {{ display: flex; margin-top: -1px; }}
  .terms {{ flex: 1.35; border: 1px solid #000; border-top: none; padding: 8px 10px; font-size: 10.5px; line-height: 1.7; }}
  .totals {{ flex: 1; }}
  .totals table {{ width: 100%; border-collapse: collapse; }}
  .totals td {{ border: 1px solid #000; padding: 5px 8px; font-size: 11.5px; }}
  .totals td.tl {{ }}
  .totals td.tv {{ text-align: right; font-weight: bold; width: 110px; }}
  .totals tr.grand td {{ font-weight: bold; background: #f2f2f2; }}
  .pay {{ margin-top: 10px; width: 100%; border-collapse: collapse; }}
  .pay td {{ border: 1px solid #000; padding: 5px 8px; font-size: 11px; }}
  .pay td.pv {{ text-align: right; font-weight: bold; width: 110px; }}
  .note {{ margin-top: 10px; font-size: 11px; line-height: 1.6; }}
  .signs {{ display: flex; gap: 40px; margin-top: 32px; }}
  .signs .s {{ flex: 1; text-align: center; font-size: 11px; }}
  .signs .line {{ border-top: 1px dotted #000; margin-bottom: 5px; height: 1px; }}

  @media print {{
    @page {{ size: A4 portrait; margin: 10mm; }}
    body {{ background: #fff; padding: 0; }}
    .sheet {{ width: auto; border: none; padding: 0; }}
    .noprint {{ display: none; }}
  }}
  .btn {{ display:block; width:760px; margin:14px auto 0; padding:10px; background:#c2410c;
          color:#fff; border:none; font-size:14px; cursor:pointer; font-family: var(--khmer); }}
</style>
</head>
<body onload=""setTimeout(function(){{ window.print(); }}, 300);"">
  <div class=""sheet"">

    <div class=""head"">
      <div class=""col"">
        <div class=""kv""><span class=""k"">លេខសម្រង់តម្លៃ <span class=""sl"">/</span> <span class=""cn"">報價單號</span> <span class=""sl"">/</span> <span class=""en"">Quotation No</span> :</span>
                        <span class=""v"">{Encode(r.QuotationNo)}</span></div>
      </div>
      <div class=""mid"">
        <div class=""title-km"">ប័ណ្ណបញ្ជាក់ការលក់</div>
        <div class=""title-cn cn"">銷售確認單</div>
        <div class=""title-en en"">SALES CONFIRMATION</div>
      </div>
      <div class=""col right"">
        <div class=""kv""><span class=""k"">លេខបញ្ជាលក់ <span class=""sl"">/</span> <span class=""cn"">銷售訂單號</span> <span class=""sl"">/</span> <span class=""en"">Sales Order No</span> :</span>
                        <span class=""v en"">{Encode(receiptNo)}</span></div>
        <div class=""kv""><span class=""k"">របៀបដឹកជញ្ជូន <span class=""sl"">/</span> <span class=""cn"">交貨方式</span> <span class=""sl"">/</span> <span class=""en"">Mode of Delivery</span> :</span>
                        <span class=""v en"">{Encode(r.ModeOfDelivery)}</span></div>
        <div class=""kv""><span class=""k"">ថ្ងៃទីឯកសារ <span class=""sl"">/</span> <span class=""cn"">單據日期</span> <span class=""sl"">/</span> <span class=""en"">Document Date</span> :</span>
                        <span class=""v en"">{r.Timestamp:dd-MM-yyyy}</span></div>
      </div>
    </div>

    <div class=""info"">
      <table>
        <tr>
          <td class=""lbl"">អតិថិជន <span class=""sl"">/</span> <span class=""cn"">客户</span> <span class=""sl"">/</span> <span class=""en"">Customer</span></td>
          <td class=""sep"">:</td><td class=""val"">{Encode(r.CustomerName)}</td>
        </tr>
        <tr>
          <td class=""lbl"">អាសយដ្ឋាន <span class=""sl"">/</span> <span class=""cn"">地址</span> <span class=""sl"">/</span> <span class=""en"">Address</span></td>
          <td class=""sep"">:</td><td class=""val"">{Encode(r.CustomerAddress)}</td>
        </tr>
        <tr>
          <td class=""lbl"">លេខទូរស័ព្ទ <span class=""sl"">/</span> <span class=""cn"">電話</span> <span class=""sl"">/</span> <span class=""en"">Tel</span></td>
          <td class=""sep"">:</td><td class=""val en"">{Encode(r.CustomerPhone)}</td>
        </tr>
        <tr>
          <td class=""lbl"">អាសយដ្ឋានដឹកជញ្ជូន <span class=""sl"">/</span> <span class=""cn"">送貨地址</span> <span class=""sl"">/</span> <span class=""en"">Delivery Address</span></td>
          <td class=""sep"">:</td><td class=""val"">{Encode(r.DeliveryAddress)}</td>
        </tr>
        <tr>
          <td class=""lbl"">កាលបរិច្ឆេទស្នើសុំដឹកជញ្ជូន <span class=""sl"">/</span> <span class=""cn"">要求送貨日期</span> <span class=""sl"">/</span> <span class=""en"">Requested Ship Date</span></td>
          <td class=""sep"">:</td><td class=""val en"">{Encode(r.RequestedShipDate)}</td>
        </tr>
        <tr>
          <td class=""lbl"">ទំនាក់ទំនងដឹកជញ្ជូន <span class=""sl"">/</span> <span class=""cn"">送貨聯絡</span> <span class=""sl"">/</span> <span class=""en"">Delivery Contact</span></td>
          <td class=""sep"">:</td><td class=""val"">{Encode(r.DeliveryContact)}</td>
        </tr>
      </table>
    </div>

    <table class=""items"">
      <thead>
        <tr>
          <th style=""width:38px"">
            <span class=""km"">ល.រ</span><span class=""cn"">編號</span><span class=""en"">No</span></th>
          <th>
            <span class=""km"">មុខទំនិញ</span><span class=""cn"">貨名</span><span class=""en"">DESCRIPTION</span></th>
          <th style=""width:72px"">
            <span class=""km"">ឯកតា</span><span class=""cn"">單位</span><span class=""en"">UNIT</span></th>
          <th style=""width:76px"">
            <span class=""km"">ចំនួន</span><span class=""cn"">數量(PCS)</span><span class=""en"">QTY</span></th>
          <th style=""width:86px"">
            <span class=""km"">តម្លៃរាយ</span><span class=""cn"">單價(USD)</span><span class=""en"">UNIT PRICE</span></th>
          <th style=""width:82px"">
            <span class=""km"">បញ្ចុះតម្លៃ</span><span class=""cn"">折扣</span><span class=""en"">DISCOUNT</span></th>
          <th style=""width:96px"">
            <span class=""km"">តម្លៃសរុប</span><span class=""cn"">合計</span><span class=""en"">AMOUNT</span></th>
        </tr>
      </thead>
      <tbody>{rows}
      </tbody>
    </table>

    <div class=""foot"">
      <div class=""terms"">
        <div>• ទំនិញដែលបានលក់ហើយ មិនអាចប្តូរវិញបានទេ!</div>
        <div>• <span class=""en"">Goods sold are not returnable.</span></div>
        <div>• ការវេចខ្ចប់តាមស្តង់ដារក្រុមហ៊ុន</div>
      </div>
      <div class=""totals"">
        <table>
          <tr>
            <td class=""tl"">សរុប <span class=""sl"">/</span> <span class=""cn"">總計</span> <span class=""sl"">/</span> <span class=""en"">TOTAL(USD)</span></td>
            <td class=""tv"">{r.Subtotal.ToString("N2", inv)}</td>
          </tr>
          <tr>
            <td class=""tl"">បញ្ចុះតម្លៃ <span class=""sl"">/</span> <span class=""cn"">折扣</span> <span class=""sl"">/</span> <span class=""en"">DISCOUNT(USD)</span></td>
            <td class=""tv"">{r.Discount.ToString("N2", inv)}</td>
          </tr>
          {taxRow}
          <tr class=""grand"">
            <td class=""tl"">ទឹកប្រាក់វិក្កយបត្រ <span class=""sl"">/</span> <span class=""cn"">发票金额</span> <span class=""sl"">/</span> <span class=""en"">INVOICE AMOUNT(USD)</span></td>
            <td class=""tv"">{r.Total.ToString("N2", inv)}</td>
          </tr>
        </table>
      </div>
    </div>

    <table class=""pay"">
      <tr>
        <td>វិធីបង់ប្រាក់ <span class=""sl"">/</span> <span class=""cn"">付款方式</span> <span class=""sl"">/</span> <span class=""en"">PAYMENT METHOD</span></td>
        <td class=""pv en"">{Encode(r.PaymentMethod)}</td>
        <td>ប្រាក់ដែលបានបង់ <span class=""sl"">/</span> <span class=""cn"">已付</span> <span class=""sl"">/</span> <span class=""en"">PAID(USD)</span></td>
        <td class=""pv"">{r.AmountPaid.ToString("N2", inv)}</td>
        <td>ប្រាក់ថយវិញ <span class=""sl"">/</span> <span class=""cn"">找零</span> <span class=""sl"">/</span> <span class=""en"">CHANGE(USD)</span></td>
        <td class=""pv"">{change.ToString("N2", inv)}</td>
      </tr>
    </table>

    {noteBlock}

    <div class=""signs"">
      <div class=""s""><div class=""line""></div>ហត្ថលេខាអ្នកលក់ <span class=""sl"">/</span> <span class=""cn"">賣方簽名</span> <span class=""sl"">/</span> <span class=""en"">Seller</span></div>
      <div class=""s""><div class=""line""></div>ហត្ថលេខាអ្នកទិញ <span class=""sl"">/</span> <span class=""cn"">買方簽名</span> <span class=""sl"">/</span> <span class=""en"">Customer</span></div>
    </div>
  </div>
  <button class=""btn noprint"" onclick=""window.print()"">បោះពុម្ព / Print</button>
</body>
</html>";
    }

    private static string Encode(string? value) =>
        System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    /// <summary>
    /// Compact counter invoice for walk-in customers. It deliberately avoids the
    /// delivery and customer-detail blocks used by the full sales confirmation.
    /// </summary>
    private static string BuildWalkInInvoiceHtml(ReceiptRequest r)
    {
        var inv = CultureInfo.InvariantCulture;
        var receiptNo = string.IsNullOrWhiteSpace(r.ReceiptNo)
            ? r.SaleId.ToString("000000", inv)
            : r.ReceiptNo;
        var rows = new StringBuilder();
        var index = 0;

        foreach (var line in r.Lines)
        {
            index++;
            rows.Append($@"<tr><td class=""no"">{index}</td><td>{Encode(line.Description)}</td><td class=""num"">{line.Quantity.ToString("N2", inv)}</td><td class=""num"">{line.UnitPrice.ToString("N2", inv)}</td><td class=""num"">{line.LineTotal.ToString("N2", inv)}</td></tr>");
        }

        for (var i = index; i < 15; i++)
            rows.Append($@"<tr><td class=""no"">{i + 1}</td><td></td><td></td><td></td><td></td></tr>");

        var discount = r.Discount > 0
            ? $@"<tr><td>បញ្ចុះតម្លៃ <span>/ Discount</span></td><td>{r.Discount.ToString("N2", inv)}</td></tr>"
            : string.Empty;
        var tax = r.Tax > 0
            ? $@"<tr><td>អាករ <span>/ Tax</span></td><td>{r.Tax.ToString("N2", inv)}</td></tr>"
            : string.Empty;
        var note = string.IsNullOrWhiteSpace(r.Note)
            ? string.Empty
            : $"<b>Note:</b> {Encode(r.Note)}";

        return $@"<!DOCTYPE html>
<html lang=""km""><head><meta charset=""utf-8"" />
<title>Invoice {Encode(receiptNo)}</title>
<style>
 :root {{ --khmer:'Khmer OS Battambang','Khmer OS','Khmer MN','Noto Sans Khmer',sans-serif; }}
 * {{ box-sizing:border-box; }} body {{ margin:0; padding:18px; background:#f3f4f6; color:#111; font-family:var(--khmer); font-size:12px; }}
 .sheet {{ width:760px; min-height:980px; margin:auto; padding:18px 20px; background:#fff; border:1px solid #aaa; }}
 .head {{ display:grid; grid-template-columns:1fr auto 1fr; align-items:end; min-height:84px; border-bottom:1px solid #111; padding-bottom:10px; }}
 .title {{ text-align:center; font-weight:bold; line-height:1.15; }} .title .km {{ font-size:21px; }} .title .en {{ font-family:Arial,sans-serif; font-size:15px; letter-spacing:1px; }}
 .invoice-no {{ text-align:right; font-family:Arial,sans-serif; }} .invoice-no b {{ color:#8b1d1d; font-size:17px; }}
 .date {{ display:flex; justify-content:flex-end; gap:4px; margin-top:11px; }} .line {{ display:inline-block; min-width:150px; border-bottom:1px dotted #333; text-align:center; }}
 table {{ border-collapse:collapse; width:100%; }} .items {{ margin-top:16px; }} .items th,.items td {{ border:1px solid #222; height:27px; padding:4px 6px; }} .items th {{ height:44px; text-align:center; line-height:1.1; font-weight:bold; }}
 .items th span,.totals span {{ font-family:Arial,sans-serif; font-size:10px; }} .no {{ text-align:center; width:42px; }} .num {{ text-align:right; font-family:Arial,sans-serif; }}
 .bottom {{ display:grid; grid-template-columns:1.45fr 1fr; }} .terms {{ border:1px solid #222; border-top:0; padding:12px 10px; line-height:1.9; min-height:116px; }} .totals td {{ border:1px solid #222; border-top:0; padding:5px 8px; height:28px; }} .totals td:last-child {{ width:112px; text-align:right; font-family:Arial,sans-serif; font-weight:bold; }} .grand td {{ font-weight:bold; background:#f4f4f4; }}
 .signatures {{ display:grid; grid-template-columns:repeat(4,1fr); gap:12px; margin-top:44px; text-align:center; }} .sigline {{ border-top:1px dotted #222; margin-bottom:7px; }}
 .btn {{ display:block; width:760px; margin:14px auto; padding:10px; border:0; background:#c2410c; color:#fff; font-size:14px; }}
 @media print {{ @page {{ size:A4 portrait; margin:10mm; }} body {{ padding:0; background:#fff; }} .sheet {{ width:auto; min-height:0; border:0; padding:0; }} .noprint {{ display:none; }} }}
</style></head><body onload=""setTimeout(function(){{ window.print(); }},300)""><div class=""sheet"">
 <div class=""head""><div></div><div class=""title""><div class=""km"">វិក្កយបត្រ</div><div class=""en"">INVOICE</div></div><div class=""invoice-no"">No. <b>{Encode(receiptNo)}</b></div></div>
 <div class=""date"">កាលបរិច្ឆេទ / Date: <span class=""line"">{r.Timestamp:dd-MM-yyyy}</span></div>
 <table class=""items""><thead><tr><th style=""width:42px"">ល.រ<br><span>No.</span></th><th>បរិយាយមុខទំនិញ<br><span>Name of Goods</span></th><th style=""width:84px"">ចំនួន<br><span>Quantity</span></th><th style=""width:105px"">តម្លៃឯកតា<br><span>Unit Price</span></th><th style=""width:112px"">ទឹកប្រាក់<br><span>Amount</span></th></tr></thead><tbody>{rows}</tbody></table>
 <div class=""bottom""><div class=""terms"">• ទំនិញដែលបានលក់ហើយ មិនអាចប្តូរវិញបានទេ។<br>• <span>Goods sold are not returnable.</span><br>{note}</div><table class=""totals""><tbody><tr><td>សរុប <span>/ Total</span></td><td>{r.Subtotal.ToString("N2", inv)}</td></tr>{discount}{tax}<tr class=""grand""><td>ទឹកប្រាក់សរុប <span>/ GRAND TOTAL</span></td><td>{r.Total.ToString("N2", inv)}</td></tr><tr><td>បានបង់ <span>/ Paid</span></td><td>{r.AmountPaid.ToString("N2", inv)}</td></tr></tbody></table></div>
 <div class=""signatures""><div><div class=""sigline""></div>អ្នកលក់<br><span>Seller</span></div><div><div class=""sigline""></div>អ្នកទិញ<br><span>Buyer</span></div><div><div class=""sigline""></div>អ្នកដឹក<br><span>Driver</span></div><div><div class=""sigline""></div>អ្នកទទួល<br><span>Receiver</span></div></div>
 </div><button class=""btn noprint"" onclick=""window.print()"">បោះពុម្ព / Print</button></body></html>";
    }

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
