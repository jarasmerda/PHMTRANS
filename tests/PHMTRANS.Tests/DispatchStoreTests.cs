using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PHMTRANS.Dispatch;
using Xunit;

namespace PHMTRANS.Tests;

public sealed class DispatchStoreTests
{
    private readonly MemoryDispatchStore _store = new();

    [Fact]
    public async Task Seed_covers_every_status()
    {
        var snapshot = await _store.ReadAsync(CancellationToken.None);

        Assert.Equal(3, snapshot.Vehicles.Count);
        Assert.Equal(3, snapshot.Drivers.Count);
        Assert.Equal(DispatchStatuses.All.Count, snapshot.Trips.Select(trip => trip.Status).Distinct().Count());
    }

    [Fact]
    public async Task Assigning_vehicle_and_driver_promotes_a_new_trip()
    {
        var snapshot = await _store.ReadAsync(CancellationToken.None);
        var trip = snapshot.Trips.Single(item => item.Status == DispatchStatuses.Nova);

        var onlyVehicle = await _store.AssignAsync(trip.Id, snapshot.Vehicles[0].Id, null, CancellationToken.None);
        var partial = Assert.IsType<DispatchMutation.Success>(onlyVehicle).Trip;
        Assert.Equal(DispatchStatuses.Nova, partial.Status);

        var both = await _store.AssignAsync(trip.Id, snapshot.Vehicles[0].Id, snapshot.Drivers[0].Id, CancellationToken.None);
        var assigned = Assert.IsType<DispatchMutation.Success>(both).Trip;
        Assert.Equal(DispatchStatuses.Prirazena, assigned.Status);
        Assert.Equal(snapshot.Vehicles[0].Id, assigned.VehicleId);
    }

    [Fact]
    public async Task Clearing_one_side_returns_assigned_trip_to_new()
    {
        var snapshot = await _store.ReadAsync(CancellationToken.None);
        var trip = snapshot.Trips.Single(item => item.Status == DispatchStatuses.Prirazena);

        var cleared = await _store.AssignAsync(trip.Id, trip.VehicleId, "", CancellationToken.None);
        var updated = Assert.IsType<DispatchMutation.Success>(cleared).Trip;

        Assert.Equal(DispatchStatuses.Nova, updated.Status);
        Assert.Null(updated.DriverId);
        Assert.Equal(trip.VehicleId, updated.VehicleId);
    }

    [Fact]
    public async Task Assignment_does_not_change_a_trip_already_on_the_road()
    {
        var snapshot = await _store.ReadAsync(CancellationToken.None);
        var trip = snapshot.Trips.Single(item => item.Status == DispatchStatuses.NaCeste);
        var otherVehicle = snapshot.Vehicles.Single(vehicle => vehicle.Id != trip.VehicleId && vehicle.Id == "veh-3");

        var result = await _store.AssignAsync(trip.Id, otherVehicle.Id, trip.DriverId, CancellationToken.None);
        var updated = Assert.IsType<DispatchMutation.Success>(result).Trip;

        Assert.Equal(DispatchStatuses.NaCeste, updated.Status);
        Assert.Equal(otherVehicle.Id, updated.VehicleId);
    }

    [Fact]
    public async Task Unknown_vehicle_is_rejected_and_state_stays()
    {
        var before = await _store.ReadAsync(CancellationToken.None);
        var trip = before.Trips.Single(item => item.Status == DispatchStatuses.Nova);

        var result = await _store.AssignAsync(trip.Id, "veh-missing", before.Drivers[0].Id, CancellationToken.None);

        Assert.IsType<DispatchMutation.Rejected>(result);
        var after = await _store.ReadAsync(CancellationToken.None);
        Assert.Equal(before.Trips.Single(item => item.Id == trip.Id).VehicleId, after.Trips.Single(item => item.Id == trip.Id).VehicleId);
        Assert.Equal(DispatchStatuses.Nova, after.Trips.Single(item => item.Id == trip.Id).Status);
    }

    [Fact]
    public async Task Status_change_is_visible_on_the_next_read()
    {
        var snapshot = await _store.ReadAsync(CancellationToken.None);
        var trip = snapshot.Trips.Single(item => item.Status == DispatchStatuses.Zrusena);

        var result = await _store.SetStatusAsync(trip.Id, DispatchStatuses.Nova, CancellationToken.None);
        Assert.IsType<DispatchMutation.Success>(result);

        var again = await _store.ReadAsync(CancellationToken.None);
        Assert.Equal(DispatchStatuses.Nova, again.Trips.Single(item => item.Id == trip.Id).Status);
    }

    [Fact]
    public async Task New_trip_gets_the_next_code_and_rejects_an_empty_customer()
    {
        var rejected = await _store.AddTripAsync(
            new NewTripRequest(null, "  ", "Sklad", "ČS", "nafta", DateTimeOffset.Now, null),
            CancellationToken.None);
        Assert.IsType<DispatchMutation.Rejected>(rejected);

        var created = await _store.AddTripAsync(
            new NewTripRequest("Z-1", "Zákazník Test", "Sklad", "ČS", "nafta", DateTimeOffset.Now, "poznámka"),
            CancellationToken.None);
        var trip = Assert.IsType<DispatchMutation.Success>(created).Trip;
        var year = DateTime.Now.Year;
        Assert.Equal(year == 2026 ? "J2026-0145" : $"J{year}-0001", trip.Code);
        Assert.Equal(DispatchStatuses.Nova, trip.Status);
        Assert.Null(trip.VehicleId);
        Assert.Null(trip.DriverId);
    }

    [Fact]
    public async Task New_trip_stores_optional_vehicle_and_driver_and_promotes_when_both_are_set()
    {
        var snapshot = await _store.ReadAsync(CancellationToken.None);
        var vehicle = snapshot.Vehicles[0];
        var driver = snapshot.Drivers[1];
        var when = DateTimeOffset.Now;

        var blank = await _store.AddTripAsync(
            new NewTripRequest(null, "Zákazník", "Sklad", "ČS", null, when, null, " ", ""),
            CancellationToken.None);
        var unassigned = Assert.IsType<DispatchMutation.Success>(blank).Trip;
        Assert.Null(unassigned.VehicleId);
        Assert.Null(unassigned.DriverId);
        Assert.Equal(DispatchStatuses.Nova, unassigned.Status);

        var onlyVehicle = await _store.AddTripAsync(
            new NewTripRequest(null, "Zákazník", "Sklad", "ČS", "nafta", when, null, vehicle.Id, null),
            CancellationToken.None);
        var partial = Assert.IsType<DispatchMutation.Success>(onlyVehicle).Trip;
        Assert.Equal(vehicle.Id, partial.VehicleId);
        Assert.Null(partial.DriverId);
        Assert.Equal(DispatchStatuses.Nova, partial.Status);

        var both = await _store.AddTripAsync(
            new NewTripRequest(null, "Zákazník", "Sklad", "ČS", "nafta", when, null, vehicle.Id, driver.Id),
            CancellationToken.None);
        var assigned = Assert.IsType<DispatchMutation.Success>(both).Trip;
        Assert.Equal(DispatchStatuses.Prirazena, assigned.Status);
        Assert.Equal(vehicle.Id, assigned.VehicleId);
        Assert.Equal(driver.Id, assigned.DriverId);

        var again = await _store.ReadAsync(CancellationToken.None);
        var stored = again.Trips.Single(trip => trip.Id == assigned.Id);
        Assert.Equal(DispatchStatuses.Prirazena, stored.Status);
        Assert.Equal(driver.Id, stored.DriverId);

        var unknownVehicle = await _store.AddTripAsync(
            new NewTripRequest(null, "Zákazník", "Sklad", "ČS", null, when, null, "veh-missing", driver.Id),
            CancellationToken.None);
        Assert.Equal("Neznámé vozidlo.", Assert.IsType<DispatchMutation.Rejected>(unknownVehicle).Message);

        var unknownDriver = await _store.AddTripAsync(
            new NewTripRequest(null, "Zákazník", "Sklad", "ČS", null, when, null, vehicle.Id, "drv-missing"),
            CancellationToken.None);
        Assert.Equal("Neznámý řidič.", Assert.IsType<DispatchMutation.Rejected>(unknownDriver).Message);

        var afterRejects = await _store.ReadAsync(CancellationToken.None);
        Assert.Equal(again.Trips.Count, afterRejects.Trips.Count);
    }

    [Fact]
    public async Task Update_saves_vehicle_driver_and_promotes_a_new_trip()
    {
        var snapshot = await _store.ReadAsync(CancellationToken.None);
        var trip = snapshot.Trips.Single(item => item.Status == DispatchStatuses.Nova);
        var vehicle = snapshot.Vehicles[0];
        var driver = snapshot.Drivers[1];
        var when = trip.PlannedStart.AddHours(2);

        var result = await _store.UpdateTripAsync(trip.Id, new UpdateTripRequest(
            "Z-edit",
            "  Zákazník po úpravě  ",
            "Sklad Kralupy",
            "ČS Liberec",
            "nafta",
            when,
            "nová poznámka",
            vehicle.Id,
            driver.Id,
            trip.Status), CancellationToken.None);
        var updated = Assert.IsType<DispatchMutation.Success>(result).Trip;

        Assert.Equal(trip.Code, updated.Code);
        Assert.Equal(DispatchStatuses.Prirazena, updated.Status);
        Assert.Equal(vehicle.Id, updated.VehicleId);
        Assert.Equal(driver.Id, updated.DriverId);
        Assert.Equal("Zákazník po úpravě", updated.Customer);
        Assert.Equal("Z-edit", updated.OrderCode);
        Assert.Equal("Sklad Kralupy", updated.LoadingPlace);
        Assert.Equal("ČS Liberec", updated.UnloadingPlace);
        Assert.Equal("nová poznámka", updated.Note);
        Assert.Equal(when, updated.PlannedStart);

        var again = await _store.ReadAsync(CancellationToken.None);
        var stored = again.Trips.Single(item => item.Id == trip.Id);
        Assert.Equal(DispatchStatuses.Prirazena, stored.Status);
        Assert.Equal(driver.Id, stored.DriverId);
        Assert.Equal("Zákazník po úpravě", stored.Customer);
    }

    [Fact]
    public async Task Update_clears_one_side_and_returns_assigned_trip_to_new()
    {
        var snapshot = await _store.ReadAsync(CancellationToken.None);
        var trip = snapshot.Trips.Single(item => item.Status == DispatchStatuses.Prirazena);

        var result = await _store.UpdateTripAsync(trip.Id, Copy(trip) with { DriverId = "  " }, CancellationToken.None);
        var updated = Assert.IsType<DispatchMutation.Success>(result).Trip;

        Assert.Equal(DispatchStatuses.Nova, updated.Status);
        Assert.Null(updated.DriverId);
        Assert.Equal(trip.VehicleId, updated.VehicleId);
        Assert.Equal(trip.Code, updated.Code);
    }

    [Fact]
    public async Task Update_keeps_status_of_a_trip_already_on_the_road()
    {
        var snapshot = await _store.ReadAsync(CancellationToken.None);
        var trip = snapshot.Trips.Single(item => item.Status == DispatchStatuses.NaCeste);
        var otherVehicle = snapshot.Vehicles.Single(vehicle => vehicle.Id == "veh-3");

        var result = await _store.UpdateTripAsync(
            trip.Id,
            Copy(trip) with { VehicleId = otherVehicle.Id, Status = trip.Status },
            CancellationToken.None);
        var updated = Assert.IsType<DispatchMutation.Success>(result).Trip;

        Assert.Equal(DispatchStatuses.NaCeste, updated.Status);
        Assert.Equal(otherVehicle.Id, updated.VehicleId);
        Assert.Equal(trip.DriverId, updated.DriverId);
    }

    [Fact]
    public async Task Update_keeps_an_explicit_status_and_rejects_unknown_values()
    {
        var snapshot = await _store.ReadAsync(CancellationToken.None);
        var trip = snapshot.Trips.Single(item => item.Status == DispatchStatuses.Nova);

        var cancelled = await _store.UpdateTripAsync(
            trip.Id,
            Copy(trip) with { VehicleId = snapshot.Vehicles[0].Id, DriverId = snapshot.Drivers[0].Id, Status = DispatchStatuses.Zrusena },
            CancellationToken.None);
        var updated = Assert.IsType<DispatchMutation.Success>(cancelled).Trip;
        Assert.Equal(DispatchStatuses.Zrusena, updated.Status);
        Assert.Equal(snapshot.Vehicles[0].Id, updated.VehicleId);

        var unknown = await _store.UpdateTripAsync(trip.Id, Copy(trip) with { Status = "Hotovo" }, CancellationToken.None);
        Assert.Equal(DispatchStatuses.UnknownMessage, Assert.IsType<DispatchMutation.Rejected>(unknown).Message);
        var after = await _store.ReadAsync(CancellationToken.None);
        Assert.Equal(DispatchStatuses.Zrusena, after.Trips.Single(item => item.Id == trip.Id).Status);
    }

    [Fact]
    public async Task Update_rejects_unknown_ids_and_a_missing_trip()
    {
        var before = await _store.ReadAsync(CancellationToken.None);
        var trip = before.Trips.Single(item => item.Status == DispatchStatuses.Nova);

        var vehicle = await _store.UpdateTripAsync(trip.Id, Copy(trip) with { VehicleId = "veh-missing" }, CancellationToken.None);
        Assert.Equal("Neznámé vozidlo.", Assert.IsType<DispatchMutation.Rejected>(vehicle).Message);

        var driver = await _store.UpdateTripAsync(trip.Id, Copy(trip) with { DriverId = "drv-missing" }, CancellationToken.None);
        Assert.Equal("Neznámý řidič.", Assert.IsType<DispatchMutation.Rejected>(driver).Message);

        var missing = await _store.UpdateTripAsync("trip-missing", Copy(trip), CancellationToken.None);
        Assert.IsType<DispatchMutation.NotFound>(missing);

        var emptyCustomer = await _store.UpdateTripAsync(trip.Id, Copy(trip) with { Customer = " " }, CancellationToken.None);
        Assert.Equal("Vyplňte zákazníka.", Assert.IsType<DispatchMutation.Rejected>(emptyCustomer).Message);

        var after = await _store.ReadAsync(CancellationToken.None);
        var stored = after.Trips.Single(item => item.Id == trip.Id);
        Assert.Equal(trip.Customer, stored.Customer);
        Assert.Equal(trip.VehicleId, stored.VehicleId);
        Assert.Equal(trip.DriverId, stored.DriverId);
        Assert.Equal(DispatchStatuses.Nova, stored.Status);
        Assert.Equal(before.Trips.Count, after.Trips.Count);
    }

    [Fact]
    public void New_trip_json_binds_optional_vehicle_and_driver()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var omitted = JsonSerializer.Deserialize<NewTripRequest>(
            """{"customer":"A","loadingPlace":"B","unloadingPlace":"C","plannedStart":"2026-11-02T06:00:00Z"}""",
            options);
        Assert.NotNull(omitted);
        Assert.Null(omitted.VehicleId);
        Assert.Null(omitted.DriverId);

        var assigned = JsonSerializer.Deserialize<NewTripRequest>(
            """{"customer":"A","loadingPlace":"B","unloadingPlace":"C","plannedStart":"2026-11-02T06:00:00Z","vehicleId":"veh-1","driverId":"drv-1"}""",
            options);
        Assert.Equal("veh-1", assigned!.VehicleId);
        Assert.Equal("drv-1", assigned.DriverId);
    }

    [Fact]
    public void Update_trip_json_binds_vehicle_driver_and_status()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var parsed = JsonSerializer.Deserialize<UpdateTripRequest>(
            """
            {
              "customer":"A",
              "loadingPlace":"B",
              "unloadingPlace":"C",
              "plannedStart":"2026-11-02T06:00:00Z",
              "vehicleId":"veh-2",
              "driverId":"",
              "status":"Nová"
            }
            """,
            options);

        Assert.NotNull(parsed);
        Assert.Equal("veh-2", parsed.VehicleId);
        Assert.Equal("", parsed.DriverId);
        Assert.Equal("Nová", parsed.Status);
        Assert.Null(parsed.OrderCode);
    }

    private static UpdateTripRequest Copy(DispatchTrip trip) =>
        new(
            trip.OrderCode,
            trip.Customer,
            trip.LoadingPlace,
            trip.UnloadingPlace,
            trip.Cargo,
            trip.PlannedStart,
            trip.Note,
            trip.VehicleId,
            trip.DriverId,
            trip.Status);
}

public sealed class DispatchPageTests
{
    [Fact]
    public void New_trip_form_offers_vehicle_and_driver_and_the_table_names_them()
    {
        var root = FindWwwRoot();
        var html = File.ReadAllText(Path.Combine(root, "dispecink.html"));
        var js = File.ReadAllText(Path.Combine(root, "dispecink.js"));

        Assert.Contains("<th>Vozidlo</th>", html, StringComparison.Ordinal);
        Assert.Contains("<th>Řidič</th>", html, StringComparison.Ordinal);
        Assert.Contains("<label>Vozidlo", html, StringComparison.Ordinal);
        Assert.Contains("<label>Řidič", html, StringComparison.Ordinal);
        Assert.Contains("id=\"vehicle\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"driver\"", html, StringComparison.Ordinal);
        Assert.Contains("— nepřiřazeno —", html, StringComparison.Ordinal);
        Assert.Contains("dispecink.js?v=5", html, StringComparison.Ordinal);
        Assert.Contains("dispecink-orders.js?v=2", html, StringComparison.Ordinal);
        Assert.Contains("id=\"order-sync-warning\"", html, StringComparison.Ordinal);
        Assert.Contains("Potvrdit k fakturaci", html, StringComparison.Ordinal);
        Assert.Contains("Fronta Pohody", html, StringComparison.Ordinal);
        Assert.Contains(">Upravit</button>", js, StringComparison.Ordinal);
        Assert.Contains("Uložit změny", js, StringComparison.Ordinal);
        Assert.Contains("data-field=\"vehicle\"", js, StringComparison.Ordinal);
        Assert.Contains("data-field=\"driver\"", js, StringComparison.Ordinal);
        Assert.Contains("}, \"PUT\")", js, StringComparison.Ordinal);
        Assert.Contains("cache: \"no-store\"", js, StringComparison.Ordinal);
        Assert.Contains("/api/dispatch/vehicles", js, StringComparison.Ordinal);
        Assert.Contains("/api/dispatch/drivers", js, StringComparison.Ordinal);
        Assert.Contains("— nepřiřazeno —", js, StringComparison.Ordinal);
        Assert.Contains("vehicleId: document.querySelector(\"#vehicle\").value", js, StringComparison.Ordinal);
        Assert.Contains("driverId: document.querySelector(\"#driver\").value", js, StringComparison.Ordinal);
        Assert.Contains("item.plate} · ${item.label}", js, StringComparison.Ordinal);
        Assert.Contains("item.name", js, StringComparison.Ordinal);
    }

    private static string FindWwwRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var wwwroot = Path.Combine(dir.FullName, "wwwroot");
            if (File.Exists(Path.Combine(wwwroot, "dispecink.html")))
                return wwwroot;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Nenalezen obsah aplikace.");
    }
}

public sealed class DispatchSqlGuardTests
{
    [Fact]
    public void Schema_script_creates_missing_tables_and_adds_missing_trip_columns()
    {
        var script = DispatchSchema.Script;

        Assert.Contains("dbo.Disp_Vehicle", script, StringComparison.Ordinal);
        Assert.Contains("dbo.Disp_Driver", script, StringComparison.Ordinal);
        Assert.Contains("dbo.Disp_Trip", script, StringComparison.Ordinal);
        Assert.Equal(6, Count(script, "IF OBJECT_ID"));
        Assert.Contains("dbo.Disp_Order", script, StringComparison.Ordinal);
        Assert.Contains("dbo.Disp_OrderVersion", script, StringComparison.Ordinal);
        Assert.Contains("dbo.Disp_SyncQueue", script, StringComparison.Ordinal);
        Assert.Contains("IF COL_LENGTH(N'dbo.Disp_Trip', N'VehicleId') IS NULL", script, StringComparison.Ordinal);
        Assert.Contains("IF COL_LENGTH(N'dbo.Disp_Trip', N'DriverId') IS NULL", script, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE dbo.Disp_Trip ADD VehicleId nvarchar(64) NULL", script, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE dbo.Disp_Trip ADD DriverId nvarchar(64) NULL", script, StringComparison.Ordinal);
        Assert.Contains("IF COL_LENGTH(N'dbo.Disp_SyncQueue', N'Warning') IS NULL", script, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE dbo.Disp_SyncQueue ADD Warning nvarchar(1000) NULL", script, StringComparison.Ordinal);
        Assert.Equal(3, Count(script, "ALTER TABLE"));
        Assert.DoesNotContain("DROP", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TRUNCATE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Smerco_", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PohodaSQL", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PohodaOrderId", script, StringComparison.Ordinal);
        Assert.Contains("PohodaInvoiceNumber", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_connection_string_does_not_open_sql_and_explains_the_gap()
    {
        var store = new SqlDispatchStore(new ConfigurationBuilder().Build(), NullLogger<SqlDispatchStore>.Instance);

        var read = await Assert.ThrowsAsync<DispatchStoreException>(() => store.ReadAsync(CancellationToken.None));
        var created = await Assert.ThrowsAsync<DispatchStoreException>(() => store.AddTripAsync(
            new NewTripRequest(null, "Zákazník", "Sklad", "ČS", null, DateTimeOffset.Now, null),
            CancellationToken.None));
        var updated = await Assert.ThrowsAsync<DispatchStoreException>(() => store.UpdateTripAsync(
            "trip-1",
            new UpdateTripRequest(null, "Zákazník", "Sklad", "ČS", null, DateTimeOffset.Now, null, "veh-1", "drv-1"),
            CancellationToken.None));

        Assert.Contains("ConnectionStrings:Dispatch", read.Message, StringComparison.Ordinal);
        Assert.Equal(read.Message, created.Message);
        Assert.Equal(read.Message, updated.Message);
        await store.TryInitializeAsync(CancellationToken.None);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var start = 0;
        while ((start = text.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }

        return count;
    }
}
