using System;
using System.Collections.Generic;
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
    private readonly string? _signedInPhone;

    public MaterialSelectionViewModel() : this(new DatabaseService()) { }

    public MaterialSelectionViewModel(DatabaseService db, TursoSyncService? sync = null, string? signedInPhone = null)
    {
        _db = db;
        _sync = sync;
        _signedInPhone = signedInPhone;
        PaymentMethods = new ObservableCollection<string> { "Cash", "Card", "Bank Transfer" };
        Units = new ObservableCollection<string> { "ea", "mm", "cm", "dm", "m", "in", "ft", "cm²", "dm²", "m²", "sheet", "box", "pair", "kg", "roll" };
        Languages = new ObservableCollection<string> { "English", "ខ្មែរ (កម្ពុជា)", "Chinese", "Vietnamese" };
        Users = new ObservableCollection<string>();
        BackupIntervals = new ObservableCollection<string>
        {
            "10 minutes", "15 minutes", "30 minutes", "45 minutes", "1 hour", "2 hours", "3 hours",
        };

        if (_sync is not null)
        {
            SyncLabel = _sync.Enabled ? "BACKUP: PENDING" : "BACKUP: OFF";
            _sync.StatusChanged += OnSyncStatusChanged;
        }

        // Show the saved auto-backup interval in the dropdown. App startup already
        // started the loop with this value, so suppress the change handler here so we
        // don't reschedule (and re-persist) it on load.
        var savedMinutes = ParseIntervalMinutes(_db.GetAppState(BackupIntervalKey)) ?? 60;
        SelectedBackupInterval = LabelForMinutes(savedMinutes);
        _applyIntervalChanges = true;

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
            LoadCatalogItems();
            LoadStock();
            LoadOrders();
            LoadUsers();
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
    [NotifyPropertyChangedFor(nameof(IsReportsSection))]
    [NotifyPropertyChangedFor(nameof(IsSettingsSection))]
    [NotifyPropertyChangedFor(nameof(IsOrderDetailSection))]
    [NotifyPropertyChangedFor(nameof(IsOrdersNavActive))]
    [NotifyPropertyChangedFor(nameof(IsCheckoutSection))]
    [NotifyPropertyChangedFor(nameof(IsCompleteSection))]
    [NotifyPropertyChangedFor(nameof(IsTechnicalPanelVisible))]
    [NotifyPropertyChangedFor(nameof(SectionTitle))]
    [NotifyPropertyChangedFor(nameof(SectionSubtitle))]
    public partial string ActiveSection { get; set; } = "Inventory";

    public bool IsInventorySection => ActiveSection == "Inventory";
    public bool IsStockSection => ActiveSection == "Stock";
    public bool IsOrdersSection => ActiveSection == "Orders";
    public bool IsReportsSection => ActiveSection == "Reports";
    public bool IsSettingsSection => ActiveSection == "Settings";
    public bool IsOrderDetailSection => ActiveSection == "OrderDetail";

    /// <summary>Keeps the Orders nav item highlighted while viewing an order's detail.</summary>
    public bool IsOrdersNavActive => IsOrdersSection || IsOrderDetailSection;
    public bool IsCheckoutSection => ActiveSection == "Checkout";

    /// <summary>The post-sale "Order Completed" confirmation screen.</summary>
    public bool IsCompleteSection => ActiveSection == "Complete";

    [ObservableProperty]
    public partial int CategoryColumns { get; set; } = 3;

    [ObservableProperty]
    public partial double CategoryCardWidth { get; set; } = 240;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTechnicalPanelVisible))]
    [NotifyPropertyChangedFor(nameof(CartPanelWidth))]
    [NotifyPropertyChangedFor(nameof(ShowCartRail))]
    [NotifyPropertyChangedFor(nameof(ShowCartContents))]
    public partial bool IsWideLayout { get; set; }

    /// <summary>The cart shows on the Inventory screen at any width.</summary>
    public bool IsTechnicalPanelVisible => IsInventorySection;

    /// <summary>Full cart width on a wide screen (always expanded).</summary>
    public const double CartWidePanelWidth = 300d;

    /// <summary>Width of the collapsed cart rail shown on smaller screens.</summary>
    public const double CartRailWidth = 138d;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CartPanelWidth))]
    [NotifyPropertyChangedFor(nameof(ShowCartRail))]
    [NotifyPropertyChangedFor(nameof(ShowCartContents))]
    public partial bool IsCartExpanded { get; set; }

    /// <summary>Width the rail grows to when focused on a smaller screen (~half the window).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CartPanelWidth))]
    public partial double CartExpandedWidth { get; set; } = 460d;

    /// <summary>
    /// Wide screens keep the cart permanently open; smaller screens show a thin rail
    /// that grows to half the window when focused.
    /// </summary>
    public double CartPanelWidth => IsWideLayout
        ? CartWidePanelWidth
        : (IsCartExpanded ? CartExpandedWidth : CartRailWidth);

    /// <summary>Show the thin rail only on a smaller screen that isn't currently focused.</summary>
    public bool ShowCartRail => !IsWideLayout && !IsCartExpanded;

    /// <summary>Show the full cart contents on wide screens, or when the rail is focused.</summary>
    public bool ShowCartContents => IsWideLayout || IsCartExpanded;

    /// <summary>Clicking the cart rail (smaller screens) toggles between the rail and the full view.</summary>
    [RelayCommand]
    private void ToggleCartExpanded()
    {
        if (IsWideLayout)
            return;
        IsCartExpanded = !IsCartExpanded;
    }

    /// <summary>Called by the window when its width changes.</summary>
    public void UpdateResponsiveLayout(double width)
    {
        IsWideLayout = width >= 1440;
        CategoryColumns = IsWideLayout ? 4 : 3;

        // Wide screens reserve the full cart; smaller screens reserve only the thin rail.
        var reservedCartWidth = IsWideLayout ? CartWidePanelWidth : CartRailWidth;

        // 224 sidebar + reserved cart + 40 effective content inset.
        // Subtract the 16px per-card margin after dividing into equal cells.
        var gridWidth = Math.Max(720d, width - 224d - reservedCartWidth - 40d);
        CategoryCardWidth = Math.Floor(gridWidth / CategoryColumns) - 16d;

        foreach (var category in Categories)
            category.CardWidth = CategoryCardWidth;

        // On a smaller screen the focused rail occupies half the window, with a sane minimum.
        CartExpandedWidth = Math.Max(360d, Math.Floor(width * 0.5d));

        // A wide layout is always fully open, never in the collapsed/expanded rail state.
        if (IsWideLayout)
            IsCartExpanded = false;

        // Cart drawer occupies 3/10 of the window, with a sane minimum.
        CartDrawerWidth = Math.Max(320d, Math.Floor(width * 0.3d));
    }

    public string SectionTitle => IsKhmer
        ? ActiveSection switch
        {
            "Stock" => "ការគ្រប់គ្រងស្តុក",
            "Orders" => "ការបញ្ជាទិញ និងប្រវត្តិ",
            "Reports" => "របាយការណ៍",
            "Settings" => "ការកំណត់",
            "OrderDetail" => "ព័ត៌មានលម្អិតការបញ្ជាទិញ",
            "Checkout" => "បង់ប្រាក់",
            "Complete" => "ការបញ្ជាទិញបានបញ្ចប់",
            _ => "ការជ្រើសរើសសម្ភារៈ",
        }
        : ActiveSection switch
        {
            "Stock" => "Stock Management",
            "Orders" => "Orders & History",
            "Reports" => "Reports",
            "Settings" => "Settings",
            "OrderDetail" => "Order Detail",
            "Checkout" => "Checkout",
            "Complete" => "Order Completed",
            _ => "Material Selection",
        };

    public string SectionSubtitle => ActiveSection switch
    {
        "Stock" => IsKhmer ? "បន្ថែម កែប្រែ ឬលុបទំនិញស្តុក និងបង្កើតទំនិញលោហៈផ្ទាល់ខ្លួន។" : "Insert, update, or delete inventory items and create custom metal objects.",
        "Orders" => IsKhmer ? "ពិនិត្យការលក់ដែលបានបញ្ចប់ និងបោះពុម្ពបង្កាន់ដៃឡើងវិញ។" : "Review completed sales and reprint receipts.",
        "Reports" => IsKhmer ? "សង្ខេបការបញ្ជាទិញ និងការលក់តាមថ្ងៃ សប្តាហ៍ ឬខែ។" : "Sales and units-sold breakdown by day, week, or month.",
        "Settings" => "Manage units, users, and system preferences for this workspace.",
        "OrderDetail" => "Full detail of the selected order. Reprint the receipt any time.",
        "Checkout" => "Verify quantities and pricing, add customer details, then complete the sale.",
        "Complete" => "The sale has been recorded, stock updated, and the receipt sent to print.",
        _ => IsKhmer
            ? "ជ្រើសរើសប្រភេទដើម្បីត្រង ឬរកមើលបញ្ជីទាំងមូលខាងក្រោម។"
            : "Choose a category to see stock, dimensions and pricing.",
    };

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    partial void OnSearchTextChanged(string value)
    {
        LoadStock();
        LoadCatalogItems();
    }

    [ObservableProperty]
    public partial string SyncLabel { get; set; } = "SYNC: LIVE";

    [ObservableProperty]
    public partial string BackupStatus { get; set; } = "Cloud backup: waiting for first run.";

    // ---- Auto-backup interval ----
    private const string BackupIntervalKey = "BackupIntervalMinutes";
    private bool _applyIntervalChanges;

    /// <summary>Interval choices offered in the Backup settings tab.</summary>
    public ObservableCollection<string> BackupIntervals { get; }

    [ObservableProperty]
    public partial string SelectedBackupInterval { get; set; } = "1 hour";

    partial void OnSelectedBackupIntervalChanged(string value)
    {
        // Ignore the initial assignment during construction.
        if (!_applyIntervalChanges)
            return;

        var minutes = MinutesForLabel(value);
        _sync?.SetInterval(TimeSpan.FromMinutes(minutes));
        try { _db.SetAppState(BackupIntervalKey, minutes.ToString(CultureInfo.InvariantCulture)); }
        catch { /* persistence is best-effort; the interval still applies this session */ }
    }

    private static int MinutesForLabel(string? label) => label switch
    {
        "10 minutes" => 10,
        "15 minutes" => 15,
        "30 minutes" => 30,
        "45 minutes" => 45,
        "1 hour" => 60,
        "2 hours" => 120,
        "3 hours" => 180,
        _ => 60,
    };

    private static string LabelForMinutes(int minutes) => minutes switch
    {
        10 => "10 minutes",
        15 => "15 minutes",
        30 => "30 minutes",
        45 => "45 minutes",
        60 => "1 hour",
        120 => "2 hours",
        180 => "3 hours",
        _ => "1 hour",
    };

    private static int? ParseIntervalMinutes(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) && m > 0 ? m : null;

    // ==================== Settings ====================

    public ObservableCollection<string> Languages { get; }
    public ObservableCollection<string> Users { get; }
    public event Action? SignOutRequested;
    public bool CanManageUsers => AuthService.IsPrimaryAdministrator(_signedInPhone);
    public bool IsUsersAccessRestricted => !CanManageUsers;
    private const string KhmerLanguage = "ខ្មែរ (កម្ពុជា)";

    [ObservableProperty]
    public partial string SelectedLanguage { get; set; } = "English";

    public bool IsKhmer => SelectedLanguage == KhmerLanguage;
    public string NavInventoryText => IsKhmer ? "សារពើភ័ណ្ឌ" : "Inventory";
    public string NavStockText => IsKhmer ? "ស្តុក" : "Stock";
    public string NavOrdersText => IsKhmer ? "ការបញ្ជាទិញ" : "Orders";
    public string NavReportsText => IsKhmer ? "របាយការណ៍" : "Reports";
    public string AdminUserText => IsKhmer ? "អ្នកគ្រប់គ្រង" : "Admin User";
    public string SettingsCenterText => IsKhmer ? "មជ្ឈមណ្ឌលការកំណត់" : "Settings Center";
    public string SettingsDescriptionText => IsKhmer ? "កំណត់រចនាសម្ព័ន្ធកន្លែងធ្វើការរបស់អ្នកនៅទីនេះ។" : "Configure this workspace in one place.";
    public string SettingsNavText => IsKhmer ? "ការកំណត់" : "SETTINGS";
    public string UnitsText => IsKhmer ? "ឯកតា" : "Units";
    public string UsersText => IsKhmer ? "អ្នកប្រើប្រាស់" : "Users";
    public string SystemText => IsKhmer ? "ប្រព័ន្ធ" : "System";
    public string BackupText => IsKhmer ? "បម្រុងទុក" : "Backup";
    public string CloudBackupText => IsKhmer ? "បម្រុងទុកលើ Cloud" : "Cloud Backup";
    public string BackupNowText => IsKhmer ? "បម្រុងទុកឥឡូវនេះ" : "Back up now";
    public string BackupDescriptionText => IsKhmer
        ? "រក្សាទុកច្បាប់ចម្លងទិន្នន័យរបស់អ្នកទៅម៉ាស៊ីនមេ Turso ។ ទិន្នន័យក្នុងម៉ាស៊ីនគឺជាប្រភពចម្បង។"
        : "Push a full snapshot of your data to the Turso server. The local database stays the source of truth.";
    public string BackupModeText => IsKhmer
        ? "បម្រុងទុកដោយស្វ័យប្រវត្តិរៀងរាល់មួយម៉ោង ខណៈកម្មវិធីកំពុងដំណើរការ។"
        : "Runs automatically every hour while the app is open, and immediately when you press Back up now.";
    public string BackupServerText => IsKhmer ? "ម៉ាស៊ីនមេ" : "Server";
    public string BackupStateText => IsKhmer ? "ស្ថានភាព" : "Status";
    public string BackupIntervalText => IsKhmer ? "ចន្លោះពេលបម្រុងទុកដោយស្វ័យប្រវត្តិ" : "Auto-backup interval";
    public string AddUnitText => IsKhmer ? "បន្ថែមឯកតា" : "Add unit";
    public string AddUserText => IsKhmer ? "បន្ថែមអ្នកប្រើប្រាស់" : "Add user";
    public string InterfaceLanguageText => IsKhmer ? "ភាសាកម្មវិធី" : "Interface language";
    private string L(string english, string khmer) => IsKhmer ? khmer : english;
    public string StockItemText => L("ITEM", "ទំនិញ");
    public string PriceText => L("PRICE", "តម្លៃ");
    public string StockQtyText => L("STOCK", "ស្តុក");
    public string SkuText => L("SKU", "លេខកូដ");
    public string ActionsText => L("ACTIONS", "សកម្មភាព");
    public string EditText => L("Edit", "កែប្រែ");
    public string DeleteText => L("Delete", "លុប");
    public string GroupCategoryText => L("GROUP / CATEGORY", "ក្រុម / ប្រភេទ");
    public string MaterialNameText => L("MATERIAL / NAME", "សម្ភារៈ / ឈ្មោះ");
    public string DimensionSpecText => L("DIMENSION / SPEC", "ទំហំ / លក្ខណៈបច្ចេកទេស");
    public string UnitText => L("UNIT", "ឯកតា");
    public string SkuLabelText => L("SKU", "លេខកូដ");
    public string ItemPriceText => L("PRICE", "តម្លៃ");
    public string StockQtyLabelText => L("STOCK QTY", "បរិមាណស្តុក");
    public string OptionalText => L("optional", "ជាជម្រើស");
    public string CategoryPlaceholderText => L("e.g. Steel, Copper, Aluminium", "ឧ. ដែក ថ្ពាន់ អាលុយមីញ៉ូម");
    public string MaterialPlaceholderText => L("e.g. Alloy Steel Grade A36", "ឧ. ដែកលោហធាតុ Alloy Grade A36");
    public string DimensionPlaceholderText => L("e.g. 2\" x 4\"", "ឧ. 2\" x 4\"");
    public string PricePlaceholderText => L("0.00", "០.០០");
    public string StockPlaceholderText => L("0", "០");
    public string EnterDetailsText => L("Enter details for a new metal object.", "សូមបញ្ចូលព័ត៌មានសម្រាប់ទំនិញលោហៈថ្មី។");
    public string NewItemText => L("Add New Item", "បន្ថែមទំនិញថ្មី");
    public string UpdateItemText => L("Update Item", "ធ្វើបច្ចុប្បន្នភាពទំនិញ");
    public string AddItemText => L("Add Item", "បន្ថែមទំនិញ");
    public string ClearText => L("Clear", "សម្អាត");
    public string NewCategoryTitleText => L("New Category", "ប្រភេទថ្មី");
    public string NewCategoryLabelText => L("CATEGORY NAME", "ឈ្មោះប្រភេទ");
    public string SaveText => L("Save", "រក្សាទុក");
    public string CancelText => L("Cancel", "បោះបង់");
    public string SearchMaterialsPlaceholder => L("Search materials, SKU, or categories...", "ស្វែងរកសម្ភារៈ លេខកូដ ឬប្រភេទ...");
    public string LiveInventoryText => L("LIVE INVENTORY", "ស្តុកបន្តផ្ទាល់");
    public string OrdersSummaryDisplay => IsKhmer && OrdersSummary == "No sales yet." ? "មិនទាន់មានការលក់ទេ។" : OrdersSummary;
    public string SearchOrdersPlaceholder => L("Search receipt no or customer", "ស្វែងរកលេខបង្កាន់ដៃ ឬអតិថិជន");
    public string ReceiptNoText => L("RECEIPT NO", "លេខបង្កាន់ដៃ");
    public string DateText => L("DATE", "កាលបរិច្ឆេទ");
    public string CustomerText => L("CUSTOMER", "អតិថិជន");
    public string ItemsText => L("ITEMS", "ទំនិញ");
    public string TotalText => L("TOTAL", "សរុប");
    public string DailyText => L("Daily", "ប្រចាំថ្ងៃ");
    public string WeeklyText => L("Weekly", "ប្រចាំសប្តាហ៍");
    public string MonthlyText => L("Monthly", "ប្រចាំខែ");
    public string OrdersText => L("ORDERS", "ការបញ្ជាទិញ");
    public string RevenueText => L("REVENUE", "ចំណូល");
    public string ItemsSoldText => L("ITEMS SOLD", "ទំនិញបានលក់");
    public string AvgOrderText => L("AVG ORDER", "មធ្យមក្នុងការបញ្ជាទិញ");
    public string ProductTypeText => L("PRODUCT TYPE", "ប្រភេទទំនិញ");
    public string DimensionText => L("DIMENSION", "ទំហំ");
    public string SoldText => L("SOLD", "បានលក់");
    public string RevenueHeaderText => L("REVENUE", "ចំណូល");
    public string ByCategoryText => L("By Category", "តាមប្រភេទ");
    public string ViewText => L("View", "មើល");
    public string PrintReceiptText => L("Print receipt", "បោះពុម្ពបង្កាន់ដៃ");
    public string CheckoutText => L("Checkout", "បង់ប្រាក់");
    public string CustomerDetailsText => L("Customer Details", "ព័ត៌មានអតិថិជន");
    public string ContinueShoppingText => L("Continue Shopping", "បន្តជ្រើសរើសទំនិញ");
    public string CompleteSalePrintText => L("Complete Sale & Print", "បញ្ចប់ការលក់ និងបោះពុម្ព");
    public string QuickActionsText => L("QUICK ACTIONS", "សកម្មភាពរហ័ស");
    public string ReportProductSummaryDisplay => IsKhmer && ReportProductSummary == "No items sold in this period." ? "មិនមានទំនិញបានលក់ក្នុងអំឡុងពេលនេះទេ។" : ReportProductSummary;
    public string MaterialSpecsText => L("MATERIAL SPECS", "លក្ខណៈបច្ចេកទេសសម្ភារៈ");
    public string SelectDimensionsText => L("Select Dimensions", "ជ្រើសរើសទំហំ");
    public string SelectionText => L("Selection", "ការជ្រើសរើស");
    public string AddToCartText => L("Add to Cart", "បន្ថែមទៅកន្ត្រក");
    public string AddToCartUpperText => L("ADD TO CART", "បន្ថែមទៅកន្ត្រក");
    public string TechnicalDocumentationText => L("TECHNICAL DOCUMENTATION", "ឯកសារបច្ចេកទេស");
    public string CertificationText => L("Certification of Compliance", "វិញ្ញាបនបត្រអនុលោមភាព");

    [ObservableProperty]
    public partial string NewUnitText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewUserPhone { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewUserPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewUserPasswordConfirmation { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewUserRole { get; set; } = "Cashier";

    [ObservableProperty]
    public partial string SettingsStatus { get; set; } = "Settings are ready.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnitsSettingsTab))]
    [NotifyPropertyChangedFor(nameof(IsUsersSettingsTab))]
    [NotifyPropertyChangedFor(nameof(IsSystemSettingsTab))]
    [NotifyPropertyChangedFor(nameof(IsBackupSettingsTab))]
    public partial string SettingsTab { get; set; } = "Units";

    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

    public bool IsUnitsSettingsTab => SettingsTab == "Units";
    public bool IsUsersSettingsTab => SettingsTab == "Users";
    public bool IsSystemSettingsTab => SettingsTab == "System";
    public bool IsBackupSettingsTab => SettingsTab == "Backup";

    /// <summary>True when a Turso auth token/URL is configured so backups can run.</summary>
    public bool BackupEnabled => _sync?.Enabled ?? false;

    /// <summary>The remote endpoint host, safe to display (no secrets).</summary>
    public string BackupEndpoint => _sync?.Endpoint ?? "Not configured";

    [RelayCommand]
    private void OpenSettings()
    {
        SettingsTab = "Units";
        SettingsStatus = "Settings are ready.";
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private void SelectSettingsTab(string? tab)
    {
        if (tab is "Units" or "Users" or "System" or "Backup")
            SettingsTab = tab;
    }

    [RelayCommand]
    private void AddUnit()
    {
        var unit = NewUnitText.Trim();
        if (string.IsNullOrWhiteSpace(unit))
        {
            SettingsStatus = "Enter a unit name first.";
            return;
        }
        if (Units.Any(existing => string.Equals(existing, unit, StringComparison.OrdinalIgnoreCase)))
        {
            SettingsStatus = $"The unit \"{unit}\" already exists.";
            return;
        }

        Units.Add(unit);
        NewUnitText = string.Empty;
        SettingsStatus = $"Added unit \"{unit}\".";
    }

    [RelayCommand]
    private void RemoveUnit(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit) || Units.Count <= 1)
            return;
        Units.Remove(unit);
        SettingsStatus = $"Removed unit \"{unit}\".";
    }

    [RelayCommand]
    private void AddUser()
    {
        if (!CanManageUsers)
        {
            SettingsStatus = "Only the primary administrator can register phone accounts.";
            return;
        }

        var phone = AuthService.NormalizeCambodianPhone(NewUserPhone);
        var role = string.IsNullOrWhiteSpace(NewUserRole) ? "Cashier" : NewUserRole.Trim();
        if (phone is null)
        {
            SettingsStatus = "Enter a valid Cambodian phone number.";
            return;
        }
        if (NewUserPassword.Length < 8 || NewUserPassword.Length > 128)
        {
            SettingsStatus = "Use a password between 8 and 128 characters.";
            return;
        }
        if (!string.Equals(NewUserPassword, NewUserPasswordConfirmation, StringComparison.Ordinal))
        {
            SettingsStatus = "Passwords do not match.";
            return;
        }
        if (role is not ("Administrator" or "Cashier" or "Warehouse"))
        {
            SettingsStatus = "Choose Administrator, Cashier, or Warehouse.";
            return;
        }
        var registration = _db.RegisterUser(_signedInPhone!, phone, NewUserPassword, role);
        if (registration == UserRegistrationResult.DuplicatePhone)
        {
            SettingsStatus = "This phone number is already registered.";
            return;
        }
        if (registration == UserRegistrationResult.NotAuthorized)
        {
            SettingsStatus = "Only the primary administrator can register phone accounts.";
            return;
        }
        if (registration != UserRegistrationResult.Success)
        {
            SettingsStatus = "Could not register the phone account. Please try again.";
            return;
        }

        NewUserPhone = string.Empty;
        NewUserPassword = string.Empty;
        NewUserPasswordConfirmation = string.Empty;
        LoadUsers();
        SettingsStatus = "Phone account registered.";
    }

    [RelayCommand]
    private void RemoveUser(string? user)
    {
        if (string.IsNullOrWhiteSpace(user))
            return;
        SettingsStatus = "Accounts cannot be removed here.";
    }

    [RelayCommand]
    private void SignOut()
    {
        IsSettingsOpen = false;
        SignOutRequested?.Invoke();
    }

    private void LoadUsers()
    {
        Users.Clear();
        foreach (var user in _db.GetUsers())
            Users.Add($"{user.Phone} · {user.Role}");
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        OnPropertyChanged(nameof(IsKhmer));
        OnPropertyChanged(nameof(CatalogSummaryText));
        LoadCatalogItems();
        OnPropertyChanged(nameof(NavInventoryText));
        OnPropertyChanged(nameof(NavStockText));
        OnPropertyChanged(nameof(NavOrdersText));
        OnPropertyChanged(nameof(NavReportsText));
        OnPropertyChanged(nameof(AdminUserText));
        OnPropertyChanged(nameof(SettingsCenterText));
        OnPropertyChanged(nameof(SettingsDescriptionText));
        OnPropertyChanged(nameof(SettingsNavText));
        OnPropertyChanged(nameof(UnitsText));
        OnPropertyChanged(nameof(UsersText));
        OnPropertyChanged(nameof(SystemText));
        OnPropertyChanged(nameof(BackupText));
        OnPropertyChanged(nameof(CloudBackupText));
        OnPropertyChanged(nameof(BackupNowText));
        OnPropertyChanged(nameof(BackupDescriptionText));
        OnPropertyChanged(nameof(BackupModeText));
        OnPropertyChanged(nameof(BackupServerText));
        OnPropertyChanged(nameof(BackupStateText));
        OnPropertyChanged(nameof(BackupIntervalText));
        OnPropertyChanged(nameof(AddUnitText));
        OnPropertyChanged(nameof(AddUserText));
        OnPropertyChanged(nameof(InterfaceLanguageText));
        OnPropertyChanged(nameof(StockItemText));
        OnPropertyChanged(nameof(PriceText));
        OnPropertyChanged(nameof(StockQtyText));
        OnPropertyChanged(nameof(SkuText));
        OnPropertyChanged(nameof(ActionsText));
        OnPropertyChanged(nameof(EditText));
        OnPropertyChanged(nameof(DeleteText));
        OnPropertyChanged(nameof(GroupCategoryText));
        OnPropertyChanged(nameof(MaterialNameText));
        OnPropertyChanged(nameof(DimensionSpecText));
        OnPropertyChanged(nameof(UnitText));
        OnPropertyChanged(nameof(SkuLabelText));
        OnPropertyChanged(nameof(ItemPriceText));
        OnPropertyChanged(nameof(StockQtyLabelText));
        OnPropertyChanged(nameof(OptionalText));
        OnPropertyChanged(nameof(CategoryPlaceholderText));
        OnPropertyChanged(nameof(MaterialPlaceholderText));
        OnPropertyChanged(nameof(DimensionPlaceholderText));
        OnPropertyChanged(nameof(PricePlaceholderText));
        OnPropertyChanged(nameof(StockPlaceholderText));
        OnPropertyChanged(nameof(EnterDetailsText));
        OnPropertyChanged(nameof(NewItemText));
        OnPropertyChanged(nameof(UpdateItemText));
        OnPropertyChanged(nameof(AddItemText));
        OnPropertyChanged(nameof(NewCategoryTitleText));
        OnPropertyChanged(nameof(NewCategoryLabelText));
        OnPropertyChanged(nameof(SaveText));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(ClearText));
        OnPropertyChanged(nameof(SearchMaterialsPlaceholder));
        OnPropertyChanged(nameof(LiveInventoryText));
        OnPropertyChanged(nameof(OrdersSummaryDisplay));
        OnPropertyChanged(nameof(SearchOrdersPlaceholder));
        OnPropertyChanged(nameof(ReceiptNoText));
        OnPropertyChanged(nameof(DateText));
        OnPropertyChanged(nameof(CustomerText));
        OnPropertyChanged(nameof(ItemsText));
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(DailyText));
        OnPropertyChanged(nameof(WeeklyText));
        OnPropertyChanged(nameof(MonthlyText));
        OnPropertyChanged(nameof(OrdersText));
        OnPropertyChanged(nameof(RevenueText));
        OnPropertyChanged(nameof(ItemsSoldText));
        OnPropertyChanged(nameof(AvgOrderText));
        OnPropertyChanged(nameof(ProductTypeText));
        OnPropertyChanged(nameof(DimensionText));
        OnPropertyChanged(nameof(SoldText));
        OnPropertyChanged(nameof(RevenueHeaderText));
        OnPropertyChanged(nameof(ByCategoryText));
        OnPropertyChanged(nameof(ViewText));
        OnPropertyChanged(nameof(PrintReceiptText));
        OnPropertyChanged(nameof(CheckoutText));
        OnPropertyChanged(nameof(CustomerDetailsText));
        OnPropertyChanged(nameof(ContinueShoppingText));
        OnPropertyChanged(nameof(CompleteSalePrintText));
        OnPropertyChanged(nameof(QuickActionsText));
        OnPropertyChanged(nameof(ReportProductSummaryDisplay));
        OnPropertyChanged(nameof(MaterialSpecsText));
        OnPropertyChanged(nameof(SelectDimensionsText));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(AddToCartText));
        OnPropertyChanged(nameof(AddToCartUpperText));
        OnPropertyChanged(nameof(DetailStockText));
        OnPropertyChanged(nameof(DetailUnitPriceLabel));
        OnPropertyChanged(nameof(DetailQuantityLabel));
        OnPropertyChanged(nameof(DetailLineTotalTitle));
        OnPropertyChanged(nameof(TechnicalDocumentationText));
        OnPropertyChanged(nameof(CertificationText));

        foreach (var product in StockItems)
            product.IsKhmer = IsKhmer;
        foreach (var product in CategoryProducts)
            product.IsKhmer = IsKhmer;
        OnPropertyChanged(nameof(SaveButtonLabel));
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(SectionTitle));
        OnPropertyChanged(nameof(SectionSubtitle));

        SettingsStatus = value == "English"
            ? "English selected."
            : value == KhmerLanguage
                ? "បានជ្រើសរើសភាសាខ្មែរ។"
                : $"{value} selected.";
    }

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

        foreach (var c in Categories)
            c.IsFilterActive = !c.IsCustom && c.Name == SelectedCategoryFilter;
    }

    /// <summary>
    /// Products shown in the catalog grid: every product, narrowed by the search box
    /// and/or the active category filter pill. This is the "flat product list" the
    /// catalog now shows by default (categories are a filter row above it, not a
    /// separate browse-by-category step).
    /// </summary>
    public ObservableCollection<Product> CatalogItems { get; } = new();

    [ObservableProperty]
    public partial string? SelectedCategoryFilter { get; set; }

    /// <summary>Heading shown above the catalog grid, summarizing the active filter/search.</summary>
    public string CatalogSummaryText
    {
        get
        {
            var scope = SelectedCategoryFilter ?? (IsKhmer ? "គ្រប់ប្រភេទ" : "All categories");
            var count = CatalogItems.Count;
            var withSearch = string.IsNullOrWhiteSpace(SearchText)
                ? scope
                : L($"{scope} matching \"{SearchText}\"", $"{scope} ត្រូវនឹង \"{SearchText}\"");
            return L($"{withSearch} · {count} item(s)", $"{withSearch} · {count} ធាតុ");
        }
    }

    private void LoadCatalogItems()
    {
        CatalogItems.Clear();
        var items = _db.SearchProducts(SearchText);
        if (!string.IsNullOrWhiteSpace(SelectedCategoryFilter))
        {
            items = items
                .Where(p => string.Equals(p.Category, SelectedCategoryFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        foreach (var p in items)
        {
            p.CartQuantity = Cart.FirstOrDefault(line => line.ProductId == p.Id)?.Quantity ?? 0;
            CatalogItems.Add(ApplyProductLanguage(p));
        }

        OnPropertyChanged(nameof(CatalogSummaryText));
    }

    /// <summary>Toggles a category filter pill: selecting the active one clears it.</summary>
    [RelayCommand]
    private void ToggleCategoryFilter(string? categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName))
            return;

        SelectedCategoryFilter = SelectedCategoryFilter == categoryName ? null : categoryName;
        foreach (var c in Categories)
            c.IsFilterActive = !c.IsCustom && c.Name == SelectedCategoryFilter;

        LoadCatalogItems();
    }

    // ==================== Product detail drawer ====================
    // Tapping a product in the catalog grid opens a focused detail panel where the
    // unit price and quantity can be adjusted before adding to the cart.

    [ObservableProperty]
    public partial bool IsProductDetailOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailProductName))]
    [NotifyPropertyChangedFor(nameof(DetailCategoryTag))]
    [NotifyPropertyChangedFor(nameof(DetailDimensionText))]
    [NotifyPropertyChangedFor(nameof(DetailStockText))]
    [NotifyPropertyChangedFor(nameof(DetailLineTotalLabel))]
    public partial Product? DetailProduct { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailLineTotalLabel))]
    public partial string DetailUnitPriceText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailLineTotalLabel))]
    public partial int DetailQuantity { get; set; } = 1;

    public string DetailProductName => DetailProduct?.Name ?? string.Empty;
    public string DetailCategoryTag => DetailProduct?.Category ?? string.Empty;
    public string DetailDimensionText => DetailProduct?.DimensionLabel ?? string.Empty;
    public string DetailStockText => DetailProduct is null
        ? string.Empty
        : L($"{DetailProduct.Stock} {DetailProduct.Unit} available",
            $"មាន {DetailProduct.Stock} {DetailProduct.LocalizedUnit}");
    public string DetailUnitPriceLabel => L("Unit price", "តម្លៃឯកតា");
    public string DetailQuantityLabel => L("Quantity", "បរិមាណ");
    public string DetailLineTotalTitle => L("Line total", "សរុប");

    public string DetailLineTotalLabel
    {
        get
        {
            var price = double.TryParse(DetailUnitPriceText, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) && p >= 0
                ? p
                : DetailProduct?.Price ?? 0;
            return $"${price * Math.Max(0, DetailQuantity):0.00}";
        }
    }

    /// <summary>Opens the detail drawer for a single product tapped in the catalog grid.</summary>
    [RelayCommand]
    private void OpenProductQuickAdd(Product? product)
    {
        if (product is null)
            return;

        DetailProduct = ApplyProductLanguage(product);
        var existing = Cart.FirstOrDefault(line => line.ProductId == product.Id);
        DetailUnitPriceText = (existing?.UnitPrice ?? product.Price).ToString("0.00", CultureInfo.InvariantCulture);
        DetailQuantity = Math.Max(1, existing?.Quantity ?? 1);
        IsProductDetailOpen = true;
    }

    [RelayCommand]
    private void CloseProductDetail() => IsProductDetailOpen = false;

    [RelayCommand]
    private void IncrementDetailQuantity()
    {
        if (DetailProduct is not null && DetailQuantity < DetailProduct.Stock)
            DetailQuantity++;
    }

    [RelayCommand]
    private void DecrementDetailQuantity()
    {
        if (DetailQuantity > 1)
            DetailQuantity--;
    }

    /// <summary>Adds the detail-drawer product to the cart at the chosen price and quantity.</summary>
    [RelayCommand]
    private void AddDetailToCart()
    {
        var product = DetailProduct;
        if (product is null || product.Stock <= 0)
            return;

        var price = double.TryParse(DetailUnitPriceText, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) && p >= 0
            ? p
            : product.Price;
        var qty = Math.Clamp(DetailQuantity, 1, product.Stock);

        var line = EnsureCartLine(product);
        line.UnitPrice = price;
        line.Quantity = qty;
        SyncProductCartQuantity(product.Id, qty);
        RecalculateCart();

        IsProductDetailOpen = false;
    }

    private Product ApplyProductLanguage(Product product)
    {
        product.IsKhmer = IsKhmer;
        return product;
    }

    [RelayCommand]
    private void SelectSection(string? section)
    {
        if (string.IsNullOrWhiteSpace(section))
            return;
        ActiveSection = section!;
        if (IsOrdersSection)
            LoadOrders();
        if (IsReportsSection)
            LoadReport();
        if (IsStockSection)
            LoadStock();
        if (IsInventorySection)
        {
            LoadCategories();
            LoadCatalogItems();
        }
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
            CategoryProducts.Add(ApplyProductLanguage(p));
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
    [NotifyPropertyChangedFor(nameof(IsPaymentCash))]
    [NotifyPropertyChangedFor(nameof(IsPaymentCard))]
    [NotifyPropertyChangedFor(nameof(IsPaymentBank))]
    public partial string PaymentMethod { get; set; } = "Cash";

    public bool IsPaymentCash => PaymentMethod == "Cash";
    public bool IsPaymentCard => PaymentMethod == "Card";
    public bool IsPaymentBank => PaymentMethod == "Bank Transfer";

    /// <summary>Selects a payment method from the segmented control (design parity).</summary>
    [RelayCommand]
    private void SelectPaymentMethod(string? method)
    {
        if (!string.IsNullOrWhiteSpace(method))
            PaymentMethod = method;
    }

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

    /// <summary>Uses the compact invoice designed for counter / walk-in sales.</summary>
    [ObservableProperty]
    public partial bool IsWalkInInvoice { get; set; } = true;

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

    /// <summary>Customer summary line for the app UI; falls back to a walk-in label.</summary>
    public string CustomerSummary =>
        string.IsNullOrWhiteSpace(CustomerName) ? "Walk-in Customer" : CustomerName.Trim();

    /// <summary>
    /// Customer name as printed on the receipt. The receipt is Khmer-only, so an
    /// unnamed walk-in falls back to the Khmer equivalent.
    /// </summary>
    private string ReceiptCustomerName =>
        string.IsNullOrWhiteSpace(CustomerName) ? "អតិថិជនទូទៅ" : CustomerName.Trim();

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

    // ==================== Order completed screen ====================
    // A snapshot of the finished sale, taken before the cart is cleared so the
    // confirmation screen can still show what was sold.

    public ObservableCollection<CartLine> CompletedLines { get; } = new();

    [ObservableProperty] public partial long CompletedSaleId { get; set; }
    [ObservableProperty] public partial string CompletedSaleNumber { get; set; } = string.Empty;
    [ObservableProperty] public partial string CompletedTimestamp { get; set; } = string.Empty;
    [ObservableProperty] public partial string CompletedCustomer { get; set; } = string.Empty;
    [ObservableProperty] public partial string CompletedContact { get; set; } = string.Empty;
    [ObservableProperty] public partial string CompletedNote { get; set; } = string.Empty;
    [ObservableProperty] public partial bool CompletedHasContact { get; set; }
    [ObservableProperty] public partial bool CompletedHasNote { get; set; }
    [ObservableProperty] public partial string CompletedItemSummary { get; set; } = string.Empty;
    [ObservableProperty] public partial string CompletedSubtotalLabel { get; set; } = string.Empty;
    [ObservableProperty] public partial string CompletedDiscountLabel { get; set; } = string.Empty;
    [ObservableProperty] public partial bool CompletedHasDiscount { get; set; }
    [ObservableProperty] public partial string CompletedTaxLabel { get; set; } = string.Empty;
    [ObservableProperty] public partial bool CompletedHasTax { get; set; }
    [ObservableProperty] public partial string CompletedTotalLabel { get; set; } = string.Empty;
    [ObservableProperty] public partial string CompletedPaymentLabel { get; set; } = string.Empty;
    [ObservableProperty] public partial string CompletedPaidLabel { get; set; } = string.Empty;
    [ObservableProperty] public partial string CompletedChangeLabel { get; set; } = string.Empty;
    [ObservableProperty] public partial string CompletedReceiptPath { get; set; } = string.Empty;

    /// <summary>Starts a fresh order and returns to the product catalogue.</summary>
    [RelayCommand]
    private void StartNewOrder()
    {
        ActiveSection = "Inventory";
        LoadCategories();
        DetailSubtitle = "New order started - pick a material category.";
    }

    /// <summary>Acknowledges the completed sale and opens the orders list.</summary>
    [RelayCommand]
    private void AcknowledgeComplete()
    {
        ActiveSection = "Orders";
        LoadOrders();
    }

    /// <summary>
    /// Prints the just-completed receipt again. Regenerated from the stored sale
    /// rather than reopening the file, so it works even if the file was removed.
    /// </summary>
    [RelayCommand]
    private void ReprintReceipt() => PrintStoredSale(CompletedSaleId);

    /// <summary>
    /// Rebuilds and prints the receipt for any stored sale. Can be run as many
    /// times as needed; the sale is always read fresh from the database.
    /// </summary>
    private bool PrintStoredSale(long saleId)
    {
        if (saleId <= 0)
            return false;

        var sale = _db.GetSaleById(saleId);
        if (sale is null)
            return false;

        try
        {
            _receipt.GenerateAndPrint(ReceiptRequest.FromSale(sale));
            return true;
        }
        catch
        {
            return false;
        }
    }

    [RelayCommand]
    private void AddProductToCart(Product? product)
    {
        if (product is null || product.Stock <= 0)
            return;

        var line = EnsureCartLine(product);
        if (line.Quantity >= line.AvailableStock)
            return;
        line.Quantity++;
        SyncProductCartQuantity(product.Id, line.Quantity);
        RecalculateCart();
    }

    /// <summary>
    /// Finds the cart line for a product, creating (and wiring) a fresh zero-quantity
    /// line if none exists yet. Callers set the quantity/price after.
    /// </summary>
    private CartLine EnsureCartLine(Product product)
    {
        var existing = Cart.FirstOrDefault(c => c.ProductId == product.Id);
        if (existing is not null)
            return existing;

        var line = new CartLine
        {
            ProductId = product.Id,
            Material = string.IsNullOrWhiteSpace(product.Name) ? product.Category : product.Name,
            Dimension = product.Dimension,
            Unit = product.Unit,
            AvailableStock = product.Stock,
            UnitPrice = product.Price,
            Quantity = 0,
        };
        line.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CartLine.Quantity))
                SyncProductCartQuantity(line.ProductId, Math.Max(0, line.Quantity));
            RecalculateCart();
        };
        Cart.Add(line);
        return line;
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

        var catalogProduct = CatalogItems.FirstOrDefault(p => p.Id == productId);
        if (catalogProduct is not null)
            catalogProduct.CartQuantity = Math.Max(0, quantity);
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

        // Persist the complete order: customer, money breakdown and every line,
        // so the receipt can be rebuilt exactly at any time in the future.
        var sale = new Sale
        {
            Timestamp = timestamp,
            CustomerName = ReceiptCustomerName,
            CustomerPhone = CustomerPhone?.Trim() ?? string.Empty,
            CustomerAddress = CustomerAddress?.Trim() ?? string.Empty,
            Note = OrderNote?.Trim() ?? string.Empty,
            Subtotal = Subtotal,
            Discount = DiscountAmount,
            TaxRate = TaxRate,
            TaxAmount = TaxAmount,
            TotalAmount = total,
            AmountPaid = paid,
            ChangeDue = Math.Max(0, paid - total),
            PaymentMethod = PaymentMethod,
            InvoiceFormat = IsWalkInInvoice ? "WalkIn" : "Detailed",
            Items = lines.Select(l => new SaleItem
            {
                ProductId = l.ProductId,
                Material = l.Material,
                Dimension = l.Dimension,
                Unit = l.Unit,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                LineTotal = l.LineTotal,
            }).ToList(),
        };

        // RecordSale allocates the receipt number and fills it back onto the sale.
        long saleId = _db.RecordSale(sale);

        var receiptPath = string.Empty;
        try
        {
            receiptPath = _receipt.GenerateAndPrint(ReceiptRequest.FromSale(sale));
        }
        catch
        {
            // Receipt generation is best-effort and must not fail the sale.
        }

        var itemCount = lines.Sum(l => l.Quantity);

        // Snapshot everything the confirmation screen needs before clearing state.
        CaptureCompletedSale(sale, lines, itemCount, receiptPath);

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
                CategoryProducts.Add(ApplyProductLanguage(p));
        }

        IsDetailOpen = false;
        IsCartDrawerOpen = false;
        ActiveSection = "Complete";
        DetailSubtitle = $"Sale #{saleId:0000} complete: {itemCount} item(s), ${total:0.00}. Receipt printed.";
    }

    /// <summary>Copies the finished sale into the confirmation-screen properties.</summary>
    private void CaptureCompletedSale(
        Sale sale, List<CartLine> lines, int itemCount, string receiptPath)
    {
        CompletedLines.Clear();
        foreach (var l in lines)
        {
            CompletedLines.Add(new CartLine
            {
                ProductId = l.ProductId,
                Material = l.Material,
                Dimension = l.Dimension,
                Unit = l.Unit,
                AvailableStock = l.AvailableStock,
                UnitPrice = l.UnitPrice,
                Quantity = l.Quantity,
            });
        }

        CompletedSaleId = sale.Id;
        CompletedSaleNumber = sale.ReceiptNoDisplay;
        CompletedTimestamp = sale.Timestamp.ToString("dddd, MMMM d, yyyy  h:mm tt");
        CompletedCustomer = sale.CustomerDisplay;
        CompletedContact = sale.ContactDisplay;
        CompletedHasContact = sale.HasContact;
        CompletedNote = sale.Note;
        CompletedHasNote = sale.HasNote;

        CompletedItemSummary = itemCount == 1 ? "1 item sold" : $"{itemCount} items sold";

        CompletedSubtotalLabel = sale.SubtotalDisplay;
        CompletedDiscountLabel = sale.DiscountDisplay;
        CompletedHasDiscount = sale.HasDiscount;
        CompletedTaxLabel = sale.TaxDisplay;
        CompletedHasTax = sale.HasTax;
        CompletedTotalLabel = sale.TotalDisplay;
        CompletedPaymentLabel = sale.PaymentMethod;
        CompletedPaidLabel = sale.AmountPaidDisplay;
        CompletedChangeLabel = sale.ChangeDueDisplay;
        CompletedReceiptPath = receiptPath;
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
    public ObservableCollection<string> CategoryChoices { get; } = new();
    public ObservableCollection<string> MaterialChoices { get; } = new();

    private const string CreateCategoryChoice = "＋ Create new group";
    private const string CreateMaterialChoice = "＋ Create new material";
    private const string CreateCategoryChoiceKhmer = "＋ បង្កើតក្រុមថ្មី";
    private const string CreateMaterialChoiceKhmer = "＋ បង្កើតសម្ភារៈថ្មី";

    [ObservableProperty]
    public partial string StockStatus { get; set; } = "Ready.";

    /// <summary>Controls the slide-in add/edit form drawer on the Stock screen.</summary>
    [ObservableProperty]
    public partial bool IsStockFormOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    [NotifyPropertyChangedFor(nameof(FormTitle))]
    [NotifyPropertyChangedFor(nameof(SaveButtonLabel))]
    [NotifyPropertyChangedFor(nameof(ShowCategoryTextInput))]
    [NotifyPropertyChangedFor(nameof(ShowMaterialTextInput))]
    public partial long EditingProductId { get; set; }

    public bool IsEditing => EditingProductId != 0;
    public string FormTitle => IsKhmer ? (IsEditing ? "កែប្រែទំនិញ" : "បន្ថែមទំនិញថ្មី") : (IsEditing ? "Edit Item" : "Add New Item");
    public string SaveButtonLabel => IsKhmer ? (IsEditing ? "ធ្វើបច្ចុប្បន្នភាពទំនិញ" : "បន្ថែមទំនិញ") : (IsEditing ? "Update Item" : "Add Item");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewCategoryEntry))]
    [NotifyPropertyChangedFor(nameof(ShowCategoryTextInput))]
    public partial string? SelectedCategoryChoice { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewMaterialEntry))]
    [NotifyPropertyChangedFor(nameof(ShowMaterialTextInput))]
    public partial string? SelectedMaterialChoice { get; set; }

    public bool IsNewCategoryEntry => SelectedCategoryChoice == CreateCategoryChoice || SelectedCategoryChoice == CreateCategoryChoiceKhmer;
    public bool IsNewMaterialEntry => SelectedMaterialChoice == CreateMaterialChoice || SelectedMaterialChoice == CreateMaterialChoiceKhmer;
    public bool ShowCategoryTextInput => IsNewCategoryEntry;
    public bool ShowMaterialTextInput => IsEditing || IsNewMaterialEntry;

    [ObservableProperty] public partial string FormCategory { get; set; } = string.Empty;
    [ObservableProperty] public partial string FormName { get; set; } = string.Empty;
    [ObservableProperty] public partial string FormDimension { get; set; } = string.Empty;
    [ObservableProperty] public partial string FormUnit { get; set; } = "ea";
    [ObservableProperty] public partial string FormSku { get; set; } = string.Empty;
    [ObservableProperty] public partial string FormPriceText { get; set; } = string.Empty;
    [ObservableProperty] public partial string FormStockText { get; set; } = string.Empty;

    /// <summary>Last real category chosen, so cancelling the "new category" modal can revert.</summary>
    private string? _lastRealCategoryChoice;

    [ObservableProperty]
    public partial bool IsNewCategoryModalOpen { get; set; }

    [ObservableProperty]
    public partial string NewCategoryName { get; set; } = string.Empty;

    partial void OnSelectedCategoryChoiceChanged(string? value)
    {
        if (IsNewCategoryChoice(value))
        {
            // Picking the "＋ Create new group" row opens a modal instead of selecting a category.
            NewCategoryName = string.Empty;
            IsNewCategoryModalOpen = true;
        }
        else if (!string.IsNullOrWhiteSpace(value))
        {
            FormCategory = value;
            _lastRealCategoryChoice = value;
        }
    }

    /// <summary>Adds the typed category to the dropdown and auto-selects it.</summary>
    [RelayCommand]
    private void SaveNewCategory()
    {
        var name = NewCategoryName.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return;

        var existing = CategoryChoices.FirstOrDefault(
            c => !IsNewCategoryChoice(c) && string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            var insertAt = CategoryChoices.Count > 0 ? CategoryChoices.Count - 1 : 0;
            CategoryChoices.Insert(insertAt, name);
            existing = name;
        }

        IsNewCategoryModalOpen = false;
        SelectedCategoryChoice = existing;
    }

    /// <summary>Closes the modal without adding, reverting the dropdown to its previous choice.</summary>
    [RelayCommand]
    private void CancelNewCategory()
    {
        IsNewCategoryModalOpen = false;
        SelectedCategoryChoice = _lastRealCategoryChoice;
    }

    partial void OnSelectedMaterialChoiceChanged(string? value)
    {
        if (!IsEditing && !string.IsNullOrWhiteSpace(value) && !IsNewMaterialChoice(value))
            FormName = value;
        else if (!IsEditing && IsNewMaterialChoice(value))
            FormName = string.Empty;
    }

    private static bool IsNewCategoryChoice(string? value) => value is CreateCategoryChoice or CreateCategoryChoiceKhmer;
    private static bool IsNewMaterialChoice(string? value) => value is CreateMaterialChoice or CreateMaterialChoiceKhmer;

    private void LoadStock()
    {
        StockItems.Clear();
        foreach (var p in _db.SearchProducts(SearchText))
            StockItems.Add(ApplyProductLanguage(p));
    }

    private void LoadProductChoices()
    {
        CategoryChoices.Clear();
        foreach (var category in _db.GetCategories().Select(c => c.Name)
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(name => name))
            CategoryChoices.Add(category);
        CategoryChoices.Add(IsKhmer ? CreateCategoryChoiceKhmer : CreateCategoryChoice);
    }

    /// <summary>Resets the form fields (used by the drawer's "Clear" button).</summary>
    [RelayCommand]
    private void NewProduct()
    {
        EditingProductId = 0;
        LoadProductChoices();
        SelectedCategoryChoice = null;
        _lastRealCategoryChoice = null;
        IsNewCategoryModalOpen = false;
        NewCategoryName = string.Empty;
        FormCategory = string.Empty;
        FormName = string.Empty;
        FormDimension = string.Empty;
        FormUnit = "ea";
        FormSku = string.Empty;
        FormPriceText = string.Empty;
        FormStockText = string.Empty;
        StockStatus = EnterDetailsText;
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
        LoadProductChoices();
        SelectedCategoryChoice = product.Category;
        SelectedMaterialChoice = product.Name;
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

    partial void OnOrdersSummaryChanged(string value) => OnPropertyChanged(nameof(OrdersSummaryDisplay));

    /// <summary>Filters the orders list by receipt number or customer name.</summary>
    [ObservableProperty]
    public partial string OrderSearchText { get; set; } = string.Empty;

    partial void OnOrderSearchTextChanged(string value) => LoadOrders();

    // ----- Order detail (full page) -----

    /// <summary>The order being inspected, loaded with all of its line items.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedOrder))]
    public partial Sale? SelectedOrder { get; set; }

    public bool HasSelectedOrder => SelectedOrder is not null;

    public ObservableCollection<SaleItem> SelectedOrderItems { get; } = new();

    [ObservableProperty]
    public partial string OrderDetailStatus { get; set; } = string.Empty;

    private void LoadOrders()
    {
        RecentSales.Clear();
        var sales = _db.GetRecentSales(200, OrderSearchText);
        foreach (var s in sales)
            RecentSales.Add(s);

        var total = sales.Sum(s => s.TotalAmount);
        if (sales.Count == 0)
        {
            OrdersSummary = string.IsNullOrWhiteSpace(OrderSearchText)
                ? "No sales yet."
                : $"No orders match \"{OrderSearchText}\".";
        }
        else
        {
            OrdersSummary = $"{sales.Count} order(s) · ${total:0.00} total revenue";
        }
    }

    /// <summary>Opens the full-page detail of a stored order.</summary>
    [RelayCommand]
    private void OpenOrderDetail(Sale? order)
    {
        if (order is null)
            return;

        // Always re-read so the detail reflects exactly what is stored.
        var full = _db.GetSaleById(order.Id);
        if (full is null)
        {
            OrderDetailStatus = "This order could not be found.";
            return;
        }

        SelectedOrder = full;
        SelectedOrderItems.Clear();
        foreach (var item in full.Items)
            SelectedOrderItems.Add(item);

        OrderDetailStatus = string.Empty;
        ActiveSection = "OrderDetail";
    }

    /// <summary>Returns from the order detail page to the orders list.</summary>
    [RelayCommand]
    private void CloseOrderDetail()
    {
        ActiveSection = "Orders";
        LoadOrders();
    }

    /// <summary>Reprints the selected order's receipt; repeatable any number of times.</summary>
    [RelayCommand]
    private void PrintSelectedOrder()
    {
        if (SelectedOrder is null)
            return;

        OrderDetailStatus = PrintStoredSale(SelectedOrder.Id)
            ? $"Receipt {SelectedOrder.ReceiptNoDisplay} sent to print at {DateTime.Now:h:mm tt}."
            : "Could not generate the receipt.";
    }

    /// <summary>Reprints directly from a row in the orders list.</summary>
    [RelayCommand]
    private void PrintOrder(Sale? order)
    {
        if (order is null)
            return;
        PrintStoredSale(order.Id);
    }

    // ==================== Reports ====================

    /// <summary>Report period: "Daily" (today), "Weekly" (this week), "Monthly" (this month).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDailyReport))]
    [NotifyPropertyChangedFor(nameof(IsWeeklyReport))]
    [NotifyPropertyChangedFor(nameof(IsMonthlyReport))]
    public partial string ReportPeriod { get; set; } = "Daily";

    public bool IsDailyReport => ReportPeriod == "Daily";
    public bool IsWeeklyReport => ReportPeriod == "Weekly";
    public bool IsMonthlyReport => ReportPeriod == "Monthly";

    [ObservableProperty]
    public partial string ReportRangeLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReportOrdersLabel { get; set; } = "0";

    [ObservableProperty]
    public partial string ReportRevenueLabel { get; set; } = "$0.00";

    [ObservableProperty]
    public partial string ReportItemsLabel { get; set; } = "0";

    [ObservableProperty]
    public partial string ReportAvgOrderLabel { get; set; } = "$0.00";

    [ObservableProperty]
    public partial string ReportDiscountLabel { get; set; } = "$0.00";

    [ObservableProperty]
    public partial string ReportProductSummary { get; set; } = string.Empty;

    partial void OnReportProductSummaryChanged(string value) => OnPropertyChanged(nameof(ReportProductSummaryDisplay));

    /// <summary>Units-sold-per-type rows for the selected period.</summary>
    public ObservableCollection<ProductSalesRow> ReportProducts { get; } = new();

    /// <summary>Units sold grouped by category for the selected period.</summary>
    public ObservableCollection<CategorySalesRow> ReportCategories { get; } = new();

    [RelayCommand]
    private void SetReportPeriod(string? period)
    {
        if (string.IsNullOrWhiteSpace(period) || period == ReportPeriod)
            return;
        ReportPeriod = period!;
        LoadReport();
    }

    /// <summary>Computes the [from, to) range for the selected period.</summary>
    private (DateTime From, DateTime To) CurrentReportRange()
    {
        var now = DateTime.Now;
        var today = now.Date;
        var tomorrow = today.AddDays(1);

        return ReportPeriod switch
        {
            "Weekly" => (StartOfWeek(today), tomorrow),
            "Monthly" => (new DateTime(today.Year, today.Month, 1), tomorrow),
            _ => (today, tomorrow),
        };
    }

    private static DateTime StartOfWeek(DateTime date)
    {
        // Week starts Monday.
        int diff = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-diff);
    }

    private void LoadReport()
    {
        var (from, to) = CurrentReportRange();
        var endInclusive = to.AddDays(-1);

        ReportRangeLabel = ReportPeriod switch
        {
            "Daily" => from.ToString("dddd, MMMM d, yyyy"),
            "Weekly" => $"{from:MMM d} – {endInclusive:MMM d, yyyy}",
            "Monthly" => from.ToString("MMMM yyyy"),
            _ => string.Empty,
        };

        var summary = _db.GetSalesSummary(from, to);
        ReportOrdersLabel = summary.OrderCountDisplay;
        ReportRevenueLabel = summary.RevenueDisplay;
        ReportItemsLabel = summary.ItemsSoldDisplay;
        ReportAvgOrderLabel = summary.AverageOrderDisplay;
        ReportDiscountLabel = summary.DiscountDisplay;

        ReportProducts.Clear();
        foreach (var row in _db.GetProductSales(from, to))
            ReportProducts.Add(row);

        ReportCategories.Clear();
        foreach (var row in _db.GetCategorySales(from, to))
            ReportCategories.Add(row);

        ReportProductSummary = ReportProducts.Count == 0
            ? "No items sold in this period."
            : $"{ReportProducts.Count} product type(s) sold";
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
