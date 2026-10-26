using ClearForge.Engine.Cleanup;
using ClearForge.Engine.Policies;
using ClearForge.Engine.Scanning;
using ClearForge.Host;
using ClearForge.Windows;

// ClearForge engine host — unelevated local IPC endpoint.
// Runs in the user's session with normal privileges; exits with the client.
var roots = WindowsRoots.CurrentUser();
var scans = new ScanSessionStore(DefaultRules.M1(), new FileScanner(roots));

var appData = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "ClearForge");
var journal = new CleanupJournal(Path.Combine(appData, "journal", "clearforge.db"));
// Session lifetime: bounds how long a scan snapshot stays executable.
// 30 min comfortably exceeds human review time while catching abandoned
// sessions whose file snapshots may have drifted. This gates EXECUTION only
// — journal restore eligibility is independent and unaffected.
var executor = new QuarantineExecutor(scans, journal,
    new QuarantineOptions(Path.Combine(appData, "quarantine"),
        SessionTtl: TimeSpan.FromMinutes(30),
        // Preview approvals expire well inside the session window so a
        // long-lived session cannot carry a stale "reviewed" state forward.
        ApprovalTtl: TimeSpan.FromMinutes(10)), roots);

// Startup reconciliation: align journal with filesystem before serving.
var attention = Recovery.Reconcile(journal);
if (attention.Count > 0)
    Console.Error.WriteLine($"recovery: {attention.Count} journal entries need review");

// M3 services: duplicates (named scopes only — never arbitrary client paths),
// read-only startup inventory, read-only browser privacy detection.
var scopes = new ClearForge.Engine.Duplicates.ScopeRegistry()
    .Add("downloads", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads")
    .Add("documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
    .Add("desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
var dupes = new ClearForge.Engine.Duplicates.DuplicateFinder(scopes);
var startup = WindowsStartupInventory.All();
var apps = ClearForge.Windows.WindowsAppInventory.All();
var privacy = new ClearForge.Engine.Privacy.BrowserPrivacyScanner(
    ClearForge.Engine.Privacy.BrowserPrivacyScanner.DefaultSpecs(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));
var cacheScan = new ClearForge.Engine.Privacy.BrowserCacheScanner(privacy);

var inventory = new ClearForge.Engine.Storage.StorageInventory(scopes);
// M4.2: bind recommendations to real session-bound candidates produced by the
// existing rule scanner and browser-cache scanner — no second mutation path.
var actionBridge = new ClearForge.Engine.Storage.RecommendationActionBridge(scans, cacheScan);

// M7.2: curated model catalogue + secure install lifecycle. The engine owns
// the model root (same directory the AI worker reads, read-only); downloads
// are HTTPS-only, digest-pinned and user-initiated — never automatic.
var models = new ClearForge.Models.ModelManager(
    Path.Combine(appData, "models"),
    ClearForge.Models.ModelCatalog.LoadBundled(),
    new ClearForge.Models.HttpModelTransport());

// M8.1 unified Smart Scan: read-only producers coordinated into one
// deduplicated result set; execution still flows exclusively through the
// preview → approval → journaled-commit pipeline on child scan sessions.
var smartScan = new ClearForge.Engine.SmartScan.SmartScanCoordinator(
    new ClearForge.Engine.SmartScan.SmartScanStore(),
    new ClearForge.Engine.SmartScan.ISmartScanProducer[]
    {
        new ClearForge.Engine.SmartScan.CatalogScanProducer(scans),
        new ClearForge.Engine.SmartScan.BrowserCacheProducer(scans, cacheScan),
        new ClearForge.Engine.SmartScan.DuplicatesProducer(scans, dupes),
        new ClearForge.Engine.SmartScan.StartupProducer(startup),
        new ClearForge.Engine.SmartScan.StorageReviewProducer(inventory),
    });

var dispatcher = new RequestDispatcher(scans, executor, journal, dupes, scopes, startup, apps, privacy, cacheScan, inventory, actionBridge, models, smartScan);
var pipeName = args.FirstOrDefault() ?? PipeServerHost.DefaultPipeName;

await using var host = new PipeServerHost(dispatcher, pipeName);
host.Start();
Console.WriteLine($"clearforge-engine listening on \\\\.\\pipe\\{pipeName} (protocol v{ClearForge.Contracts.Protocol.Version})");

await Task.Run(() => { try { while (Console.In.Read() != -1) { } } catch { } });
