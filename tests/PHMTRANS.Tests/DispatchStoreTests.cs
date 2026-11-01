using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using PHMTRANS.Dispatch;
using Xunit;

namespace PHMTRANS.Tests;

public sealed class DispatchStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("phmtrans-dispatch").FullName;
    private readonly DispatchStore _store;

    public DispatchStoreTests()
    {
        var environment = new TestHostEnvironment(_root);
        _store = new DispatchStore(environment, new ConfigurationBuilder().Build(), NullLogger<DispatchStore>.Instance);
    }

    [Fact]
    public async Task Seed_is_temporary_and_covers_every_status()
    {
        var snapshot = await _store.ReadAsync(CancellationToken.None);
        var file = StoreFile();

        Assert.StartsWith(_root, file, StringComparison.Ordinal);
        Assert.Contains("Dočasné úložiště", await File.ReadAllTextAsync(file), StringComparison.Ordinal);
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
    public async Task Unknown_vehicle_is_rejected_and_file_stays()
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
    public async Task Status_change_is_reloaded_from_disk()
    {
        var snapshot = await _store.ReadAsync(CancellationToken.None);
        var trip = snapshot.Trips.Single(item => item.Status == DispatchStatuses.Zrusena);

        var result = await _store.SetStatusAsync(trip.Id, DispatchStatuses.Nova, CancellationToken.None);
        Assert.IsType<DispatchMutation.Success>(result);

        var reloaded = new DispatchStore(new TestHostEnvironment(_root), new ConfigurationBuilder().Build(), NullLogger<DispatchStore>.Instance);
        var again = await reloaded.ReadAsync(CancellationToken.None);
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
    }

    [Fact]
    public async Task Corrupt_file_is_not_replaced_by_seed()
    {
        await _store.ReadAsync(CancellationToken.None);
        var file = StoreFile();
        await File.WriteAllTextAsync(file, "{");

        var exception = await Assert.ThrowsAsync<DispatchStoreException>(() => _store.ReadAsync(CancellationToken.None));

        Assert.Contains("načíst", exception.Message, StringComparison.Ordinal);
        Assert.Equal("{", await File.ReadAllTextAsync(file));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string StoreFile() =>
        Directory.EnumerateFiles(_root, "store.json", SearchOption.AllDirectories).Single();

    private sealed class TestHostEnvironment(string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "PHMTRANS.Tests";
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
