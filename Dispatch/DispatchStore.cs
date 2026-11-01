using System.Text.Encodings.Web;
using System.Text.Json;

namespace PHMTRANS.Dispatch;

/// <summary>
/// Dočasné úložiště dispečinku: jeden JSON soubor, ne databáze a ne Pohoda.
/// </summary>
public sealed class DispatchStore
{
    public const string StorageKind = "temporary-json";

    public const string StorageNote =
        "Dočasné úložiště: JSON soubor na disku služby, ne databáze. Na Pohodu se nezapisuje. Soubor není zálohovaný a po jeho smazání se znovu založí ukázkové jízdy.";

    private const int SupportedVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<DispatchStore> _logger;
    private readonly string _path;

    public DispatchStore(IWebHostEnvironment environment, IConfiguration configuration, ILogger<DispatchStore> logger)
        : this(ResolvePath(environment, configuration), logger)
    {
    }

    private DispatchStore(string storePath, ILogger<DispatchStore> logger)
    {
        _path = storePath;
        _logger = logger;
    }

    public async Task<DispatchSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadOrSeedAsync(cancellationToken);
            return Snapshot(document);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DispatchMutation> AddTripAsync(NewTripRequest request, CancellationToken cancellationToken)
    {
        var error = ValidateNewTrip(request);
        if (error is not null)
            return new DispatchMutation.Rejected(error);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadOrSeedAsync(cancellationToken);
            var now = DateTimeOffset.Now;
            var trip = new DispatchTrip(
                NewId(document.Trips),
                NextCode(document.Trips, now),
                Optional(request.OrderCode, 40),
                request.Customer!.Trim(),
                request.LoadingPlace!.Trim(),
                request.UnloadingPlace!.Trim(),
                Optional(request.Cargo, 80) ?? "",
                request.PlannedStart!.Value,
                DispatchStatuses.Nova,
                null,
                null,
                Optional(request.Note, 500),
                now);
            document.Trips.Add(trip);
            await SaveAsync(document, cancellationToken);
            return new DispatchMutation.Success(trip);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DispatchMutation> AssignAsync(
        string id,
        string? vehicleId,
        string? driverId,
        CancellationToken cancellationToken)
    {
        vehicleId = BlankToNull(vehicleId);
        driverId = BlankToNull(driverId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadOrSeedAsync(cancellationToken);
            var index = IndexOf(document, id);
            if (index < 0)
                return new DispatchMutation.NotFound();

            if (vehicleId is not null && document.Vehicles.All(vehicle => vehicle.Id != vehicleId))
                return new DispatchMutation.Rejected("Neznámé vozidlo.");
            if (driverId is not null && document.Drivers.All(driver => driver.Id != driverId))
                return new DispatchMutation.Rejected("Neznámý řidič.");

            var trip = document.Trips[index];
            var status = trip.Status;
            var complete = vehicleId is not null && driverId is not null;
            if (complete && status == DispatchStatuses.Nova)
                status = DispatchStatuses.Prirazena;
            else if (!complete && status == DispatchStatuses.Prirazena)
                status = DispatchStatuses.Nova;

            var updated = trip with
            {
                VehicleId = vehicleId,
                DriverId = driverId,
                Status = status,
                UpdatedAt = DateTimeOffset.Now
            };
            document.Trips[index] = updated;
            await SaveAsync(document, cancellationToken);
            return new DispatchMutation.Success(updated);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DispatchMutation> SetStatusAsync(string id, string? status, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(status))
            return new DispatchMutation.Rejected("Vyplňte stav.");

        var selected = status.Trim();
        if (!DispatchStatuses.IsKnown(selected))
            return new DispatchMutation.Rejected(DispatchStatuses.UnknownMessage);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadOrSeedAsync(cancellationToken);
            var index = IndexOf(document, id);
            if (index < 0)
                return new DispatchMutation.NotFound();

            var updated = document.Trips[index] with
            {
                Status = selected,
                UpdatedAt = DateTimeOffset.Now
            };
            document.Trips[index] = updated;
            await SaveAsync(document, cancellationToken);
            return new DispatchMutation.Success(updated);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string ResolvePath(IWebHostEnvironment environment, IConfiguration? configuration)
    {
        var configured = configuration?["Dispatch:StorePath"];
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        var siteRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "wwwroot"));
        if (Directory.Exists(siteRoot))
        {
            var besideService = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Data", "dispatch"));
            return Path.Combine(besideService, "store.json");
        }

        return Path.Combine(environment.ContentRootPath, "Data", "dispatch", "store.json");
    }

    private async Task<DispatchDocument> LoadOrSeedAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_path))
            {
                var seeded = CreateSeed();
                await SaveAsync(seeded, cancellationToken);
                _logger.LogInformation("Dispečink založil dočasný soubor {Path}", _path);
                return seeded;
            }

            await using var stream = File.OpenRead(_path);
            var document = await JsonSerializer.DeserializeAsync<DispatchDocument>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("Soubor je prázdný.");
            if (document.Version > SupportedVersion)
                throw new InvalidDataException($"Soubor má verzi {document.Version}.");

            document.Version = SupportedVersion;
            document.Vehicles ??= [];
            document.Drivers ??= [];
            document.Trips ??= [];
            return document;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException)
        {
            _logger.LogError(exception, "Dočasné úložiště dispečinku nelze načíst z {Path}", _path);
            throw new DispatchStoreException("Dočasné úložiště dispečinku se nepodařilo načíst.", exception);
        }
    }

    private async Task SaveAsync(DispatchDocument document, CancellationToken cancellationToken)
    {
        try
        {
            document.Version = SupportedVersion;
            document.Notice = StorageNote;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(document, JsonOptions);
            var temporary = _path + ".tmp";
            await File.WriteAllTextAsync(temporary, json, cancellationToken);
            File.Move(temporary, _path, overwrite: true);
        }
        catch (IOException exception)
        {
            _logger.LogError(exception, "Dočasné úložiště dispečinku nelze uložit do {Path}", _path);
            throw new DispatchStoreException("Dočasné úložiště dispečinku se nepodařilo uložit.", exception);
        }
    }

    private static DispatchSnapshot Snapshot(DispatchDocument document) =>
        new(document.Trips.ToArray(), document.Vehicles.ToArray(), document.Drivers.ToArray());

    private static int IndexOf(DispatchDocument document, string id) =>
        document.Trips.FindIndex(trip => trip.Id == id);

    private static string? ValidateNewTrip(NewTripRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Customer))
            return "Vyplňte zákazníka.";
        if (request.Customer.Trim().Length > 160)
            return "Zákazník může mít nejvýše 160 znaků.";
        if (string.IsNullOrWhiteSpace(request.LoadingPlace))
            return "Vyplňte místo nakládky.";
        if (request.LoadingPlace.Trim().Length > 160)
            return "Místo nakládky může mít nejvýše 160 znaků.";
        if (string.IsNullOrWhiteSpace(request.UnloadingPlace))
            return "Vyplňte místo vykládky.";
        if (request.UnloadingPlace.Trim().Length > 160)
            return "Místo vykládky může mít nejvýše 160 znaků.";
        if (Optional(request.Cargo, 80) is null && !string.IsNullOrWhiteSpace(request.Cargo))
            return "Náklad může mít nejvýše 80 znaků.";
        if (Optional(request.OrderCode, 40) is null && !string.IsNullOrWhiteSpace(request.OrderCode))
            return "Zakázka může mít nejvýše 40 znaků.";
        if (Optional(request.Note, 500) is null && !string.IsNullOrWhiteSpace(request.Note))
            return "Poznámka může mít nejvýše 500 znaků.";
        if (request.PlannedStart is null)
            return "Vyplňte termín jízdy.";
        return null;
    }

    private static string? Optional(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : null;
    }

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NewId(IReadOnlyList<DispatchTrip> trips)
    {
        var id = Guid.NewGuid().ToString("n");
        while (trips.Any(trip => trip.Id == id))
            id = Guid.NewGuid().ToString("n");
        return id;
    }

    private static string NextCode(IReadOnlyList<DispatchTrip> trips, DateTimeOffset now)
    {
        var prefix = $"J{now.Year}-";
        var highest = 0;
        foreach (var trip in trips)
        {
            if (!trip.Code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (int.TryParse(trip.Code.AsSpan(prefix.Length), out var number))
                highest = Math.Max(highest, number);
        }

        return $"{prefix}{(highest + 1):0000}";
    }

    private static DispatchDocument CreateSeed()
    {
        var now = DateTimeOffset.Now;
        return new DispatchDocument
        {
            Version = SupportedVersion,
            Notice = StorageNote,
            Vehicles =
            [
                new("veh-1", "1A2 3456", "Cisterna MAN 32 m³"),
                new("veh-2", "2B3 4567", "Cisterna Volvo 28 m³"),
                new("veh-3", "3C4 5678", "Cisterna Scania 36 m³")
            ],
            Drivers =
            [
                new("drv-1", "Jan Novák", "+420 601 000 001"),
                new("drv-2", "Petr Svoboda", "+420 602 000 002"),
                new("drv-3", "Martin Dvořák", "+420 603 000 003")
            ],
            Trips =
            [
                Trip("trip-5", "J2026-0140", "Z2026-0140", "Doprava Demo s.r.o.", "Sklad Mělník", "ČS Hradec Králové – ukázka", "motorová nafta", now.AddDays(-1), DispatchStatuses.Zrusena, null, null, "Ukázková jízda, zákazník zrušil", now.AddHours(-6)),
                Trip("trip-1", "J2026-0141", "Z2026-0141", "Agropodnik Demo a.s.", "Sklad Lovosice", "ČS Olomouc – ukázka", "motorová nafta", now.AddHours(-8), DispatchStatuses.Vylozena, "veh-3", "drv-3", "Ukázková jízda", now.AddHours(-2)),
                Trip("trip-2", "J2026-0142", "Z2026-0142", "Velkoobchod Palivo s.r.o.", "Sklad Cerekvice", "ČS Praha Chodov – ukázka", "motorová nafta", now.AddHours(-1), DispatchStatuses.NaCeste, "veh-1", "drv-1", "Ukázková jízda", now.AddMinutes(-30)),
                Trip("trip-3", "J2026-0143", "Z2026-0143", "Rafinérie Demo a.s.", "Areál Litvínov", "ČS Brno Slatina – ukázka", "benzin BA95", now.AddHours(3), DispatchStatuses.Nova, null, null, "Ukázková jízda, čeká na přiřazení", now),
                Trip("trip-4", "J2026-0144", "Z2026-0144", "Síť stanic Demo s.r.o.", "Sklad Kralupy", "ČS Plzeň Bory – ukázka", "motorová nafta", now.AddHours(5), DispatchStatuses.Prirazena, "veh-2", "drv-2", "Ukázková jízda", now)
            ]
        };
    }

    private static DispatchTrip Trip(
        string id,
        string code,
        string orderCode,
        string customer,
        string loadingPlace,
        string unloadingPlace,
        string cargo,
        DateTimeOffset plannedStart,
        string status,
        string? vehicleId,
        string? driverId,
        string note,
        DateTimeOffset updatedAt) =>
        new(id, code, orderCode, customer, loadingPlace, unloadingPlace, cargo, plannedStart, status, vehicleId, driverId, note, updatedAt);
}
