namespace Monitoring_API.Services;

// Startup seed + runtime watcher for the ocelot/ folder (ported from Sentinel's
// seedAllOcelotFiles + startOcelotWatcher). On boot it imports every JSON file
// present; while running, dropping a new JSON in triggers an import.
public class OcelotWatcherHostedService : BackgroundService
{
    private readonly OcelotImportService _importer;
    private readonly ILogger<OcelotWatcherHostedService> _logger;
    private readonly string _ocelotDir;
    private FileSystemWatcher? _watcher;

    public OcelotWatcherHostedService(
        OcelotImportService importer,
        IConfiguration config,
        ILogger<OcelotWatcherHostedService> logger)
    {
        _importer = importer;
        _logger = logger;
        _ocelotDir = config["Ocelot:Directory"] is { Length: > 0 } dir
            ? dir
            : Path.Combine(AppContext.BaseDirectory, "ocelot");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            Directory.CreateDirectory(_ocelotDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OCELOT] Could not create/access {Dir} — watcher disabled", _ocelotDir);
            return;
        }

        // Startup seed pass.
        foreach (var file in Directory.GetFiles(_ocelotDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            await _importer.ImportFileAsync(file, stoppingToken);
        }
        // .JSON (upper) too — Windows FS is case-insensitive but Linux containers are not.
        foreach (var file in Directory.GetFiles(_ocelotDir, "*.JSON", SearchOption.TopDirectoryOnly))
        {
            await _importer.ImportFileAsync(file, stoppingToken);
        }

        _watcher = new FileSystemWatcher(_ocelotDir)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        _watcher.Created += OnFileEvent;
        _watcher.Changed += OnFileEvent;

        _logger.LogInformation("[OCELOT] Watcher active → {Dir}", _ocelotDir);

        // Keep alive until shutdown.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            // shutting down
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (!e.Name?.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ?? true) return;

        // Debounce briefly so the file is fully written before we read it.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2000);
                await _importer.ImportFileAsync(e.FullPath, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OCELOT] Runtime import failed for {File}", e.Name);
            }
        });
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
    }
}
