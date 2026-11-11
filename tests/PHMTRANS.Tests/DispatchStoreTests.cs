using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PHMTRANS.Dispatch;
using Xunit;
using PHMTRANS;

namespace PHMTRANS.Tests;

public sealed class DispatchStoreTests
{
    private readonly MemoryDispatchStore _store = new();


















}

public sealed class DispatchPageTests
{
    [Fact]
    public void Dispatch_page_has_orders_calendar_and_no_trips()
    {
        var root = FindWwwRoot();
        var html = File.ReadAllText(Path.Combine(root, "dispecink.html"));
        var js = File.ReadAllText(Path.Combine(root, "dispecink.js"));
        var ordersJs = File.ReadAllText(Path.Combine(root, "dispecink-orders.js"));

        Assert.Contains("dispecink.js?v=14", html, StringComparison.Ordinal);
        Assert.Contains("dispecink-orders.js?v=26", html, StringComparison.Ordinal);
        Assert.Contains("/api/dispatch/calendar?from=", ordersJs, StringComparison.Ordinal);
        Assert.Contains("-driver-note", ordersJs, StringComparison.Ordinal);
        Assert.Contains("data-toggle-availability", ordersJs, StringComparison.Ordinal);
        Assert.Contains("table.avail", html, StringComparison.Ordinal);
        Assert.Contains("dispecink-tyden.js?v=2", html, StringComparison.Ordinal);
        Assert.Contains("id=\"open-week\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"week-view\" hidden", html, StringComparison.Ordinal);
        Assert.Contains("#zakazka=", ordersJs, StringComparison.Ordinal);
        Assert.Contains("data-open-week", ordersJs, StringComparison.Ordinal);
        Assert.Contains("dispecink-booking.js?v=1", html, StringComparison.Ordinal);
        Assert.Contains("data-toggle-booking", ordersJs, StringComparison.Ordinal);
        Assert.Contains("data-comp-number", ordersJs, StringComparison.Ordinal);
        Assert.Contains("closeOrderDetail();", ordersJs, StringComparison.Ordinal);
        Assert.Contains("/api/dispatch/calendar/events?from=", ordersJs, StringComparison.Ordinal);
        var bookingJs = File.ReadAllText(Path.Combine(root, "dispecink-booking.js"));
        Assert.Contains("/api/dispatch/calendar/events?from=", bookingJs, StringComparison.Ordinal);
        Assert.Contains("data-bk-track", bookingJs, StringComparison.Ordinal);
        Assert.True(html.IndexOf("id=\"order-docs\"", StringComparison.Ordinal) < html.IndexOf("id=\"order-edit\"", StringComparison.Ordinal));
        var weekJs = File.ReadAllText(Path.Combine(root, "dispecink-tyden.js"));
        Assert.Contains("/api/dispatch/calendar/events?from=", weekJs, StringComparison.Ordinal);
        Assert.Contains("multi-filter.js?v=1", html, StringComparison.Ordinal);
        Assert.Contains("filter.css?v=1", html, StringComparison.Ordinal);
        Assert.Contains("value=\"Nová\" checked", html, StringComparison.Ordinal);
        Assert.Contains("value=\"Stornováno\">", html, StringComparison.Ordinal);
        Assert.Contains("id=\"order-status-filter\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"order-create-dialog\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"open-order-create\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"open-calendar\"", html, StringComparison.Ordinal);
        Assert.Contains("Kalendář řidičů", html, StringComparison.Ordinal);
        Assert.Contains("id=\"calendar-view\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"rates-wrap\"", html, StringComparison.Ordinal);
        Assert.Contains("Potvrdit k fakturaci", html, StringComparison.Ordinal);
        Assert.Contains("Fronta Pohody", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Jízd", html, StringComparison.Ordinal);
        Assert.DoesNotContain("trip", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trip", js, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trip", ordersJs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/api/dispatch/calendar?", js, StringComparison.Ordinal);
        Assert.Contains("window.showOrder(order.dataset.calOrder)", js, StringComparison.Ordinal);
        Assert.Contains("/api/dispatch/rates", js, StringComparison.Ordinal);
        Assert.Contains("showView(\"calendar\")", ordersJs, StringComparison.Ordinal);
        Assert.Contains("#kalendar", ordersJs, StringComparison.Ordinal);
        Assert.Contains("param: \"zakazky\"", ordersJs, StringComparison.Ordinal);
        Assert.Contains("excludeByDefault: [\"Stornováno\"]", ordersJs, StringComparison.Ordinal);
    }

    [Fact]
    public void Order_list_query_includes_stops_and_legs()
    {
        var repo = Directory.GetParent(FindWwwRoot())!.FullName;
        var source = File.ReadAllText(Path.Combine(repo, "Dispatch", "SqlOrderStore.cs"));
        Assert.Contains("WHERE OrderId IN @Ids", source, StringComparison.Ordinal);
        Assert.Contains("FROM dbo.Disp_OrderStop", source, StringComparison.Ordinal);
        Assert.Contains("FROM dbo.Disp_OrderLeg", source, StringComparison.Ordinal);
    }


    [Fact]
    public void Version_history_is_a_list_of_links_to_a_read_only_snapshot()
    {
        var root = FindWwwRoot();
        var html = File.ReadAllText(Path.Combine(root, "dispecink.html"));
        var orders = File.ReadAllText(Path.Combine(root, "dispecink-orders.js"));

        Assert.Contains("id=\"version-links\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"version-page\"", html, StringComparison.Ordinal);
        Assert.Contains("Starší verze zatím nejsou.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"version-rows\"", html, StringComparison.Ordinal);
        Assert.Contains("data-version=", orders, StringComparison.Ordinal);
        Assert.Contains("Verze ${version.currentVersion} – ${orderWhen(version.updatedAt)} – ${versionStatus(data, version)}", orders, StringComparison.Ordinal);
        Assert.Contains("jen ke čtení", orders, StringComparison.Ordinal);
        Assert.Contains("Zpět na zakázku", orders, StringComparison.Ordinal);
        Assert.Contains("Dohodnutá cena", orders, StringComparison.Ordinal);
        Assert.Contains("version.stops", orders, StringComparison.Ordinal);
        Assert.Contains("version.legs", orders, StringComparison.Ordinal);
        Assert.Contains("<th>Nákladové km</th><th>Fakturované km</th>", orders, StringComparison.Ordinal);
        Assert.Contains("<span class=\"pill\">neodesláno</span>", orders, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"version-rows\"", orders, StringComparison.Ordinal);
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
    public void Vehicle_compartments_are_attached_in_order_and_invalid_rows_are_skipped()
    {
        var vehicles = SqlDispatchStore.BuildVehicles(
            [
                new SqlDispatchStore.VehicleRow("veh-a", "1AB 0001", "Cisterna A", 21.5m),
                new SqlDispatchStore.VehicleRow("veh-b", "1AB 0002", "Cisterna B", null)
            ],
            [
                new SqlDispatchStore.CompartmentRow("veh-a", 2, 6000m),
                new SqlDispatchStore.CompartmentRow("veh-a", 1, 8000m),
                new SqlDispatchStore.CompartmentRow("veh-a", 11, 5000m),
                new SqlDispatchStore.CompartmentRow("veh-a", 3, 0m),
                new SqlDispatchStore.CompartmentRow("veh-x", 1, 5000m)
            ]);

        var first = vehicles.Single(vehicle => vehicle.Id == "veh-a");
        Assert.Equal([1, 2], first.Compartments!.Select(item => item.Number));
        Assert.Equal(14000m, VehicleCompartments.TotalCapacity(first));
        Assert.Equal(21.5m, first.CostPerKm);
        Assert.Empty(vehicles.Single(vehicle => vehicle.Id == "veh-b").Compartments!);
    }

    [Fact]
    public void Schema_script_creates_missing_tables_and_adds_missing_trip_columns()
    {
        var script = DispatchSchema.Script;

        Assert.Contains("dbo.Disp_Vehicle", script, StringComparison.Ordinal);
        Assert.Contains("dbo.Disp_Driver", script, StringComparison.Ordinal);
        Assert.Contains("dbo.Disp_Trip", script, StringComparison.Ordinal);
        Assert.Equal(15, Count(script, "IF OBJECT_ID"));
        Assert.Contains("dbo.Disp_Order", script, StringComparison.Ordinal);
        Assert.Contains("dbo.Disp_OrderVersion", script, StringComparison.Ordinal);
        Assert.Contains("dbo.Disp_SyncQueue", script, StringComparison.Ordinal);
        Assert.Contains("IF COL_LENGTH(N'dbo.Disp_Trip', N'VehicleId') IS NULL", script, StringComparison.Ordinal);
        Assert.Contains("IF COL_LENGTH(N'dbo.Disp_Trip', N'DriverId') IS NULL", script, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE dbo.Disp_Trip ADD VehicleId nvarchar(64) NULL", script, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE dbo.Disp_Trip ADD DriverId nvarchar(64) NULL", script, StringComparison.Ordinal);
        Assert.Equal(44, Count(script, "ALTER TABLE"));
        Assert.Contains("ALTER TABLE dbo.Disp_Order ADD MissingDocumentsReason nvarchar(500) NULL", script, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE dbo.Disp_OrderLoad ADD Compartments nvarchar(40) NULL", script, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE dbo.Disp_Order ADD LoadingTime time(0) NULL", script, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE dbo.Disp_OrderVersion ADD UnloadingTime time(0) NULL", script, StringComparison.Ordinal);
        Assert.Contains("IF COL_LENGTH(N'dbo.Disp_Order', N'CancelReason') IS NULL", script, StringComparison.Ordinal);
        Assert.Contains("IF COL_LENGTH(N'dbo.Disp_Order', N'CustomerIco') IS NULL", script, StringComparison.Ordinal);
        Assert.Contains("IF COL_LENGTH(N'dbo.Disp_OrderVersion', N'CustomerStreet') IS NULL", script, StringComparison.Ordinal);
        Assert.DoesNotContain("dbo.AD", script, StringComparison.Ordinal);
        Assert.Equal(3, DispatchSchema.Batches.Length);
        Assert.Contains("dbo.Disp_OrderStop", script, StringComparison.Ordinal);
        Assert.Contains("dbo.Disp_OrderLeg", script, StringComparison.Ordinal);
        Assert.Contains("dbo.Disp_Setting", script, StringComparison.Ordinal);
        Assert.Contains("dbo.Disp_FuelType", script, StringComparison.Ordinal);
        Assert.Contains("dbo.Disp_Distance", script, StringComparison.Ordinal);
        Assert.Contains("IF OBJECT_ID(N'dbo.Disp_VehicleCompartment', N'U') IS NULL", script, StringComparison.Ordinal);
        Assert.Contains("CHECK (Number BETWEEN 1 AND 10)", script, StringComparison.Ordinal);
        Assert.Contains("FK_Disp_VehicleCompartment_Vehicle", script, StringComparison.Ordinal);
        Assert.Contains("IF OBJECT_ID(N'dbo.Disp_LoadingPlace', N'U') IS NULL", script, StringComparison.Ordinal);
        Assert.Contains("UX_Disp_LoadingPlace_Name", script, StringComparison.Ordinal);
        Assert.Contains("IF OBJECT_ID(N'dbo.Disp_OrderLoad', N'U') IS NULL", script, StringComparison.Ordinal);
        Assert.Contains("FK_Disp_OrderLoad_Order", script, StringComparison.Ordinal);
        Assert.Contains("IF OBJECT_ID(N'dbo.Disp_Point', N'U') IS NULL", script, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE dbo.Disp_Distance ADD BilledKm decimal(18,3) NULL", script, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE dbo.Disp_OrderLeg ADD BilledKm decimal(18,3) NULL", script, StringComparison.Ordinal);
        Assert.Contains("N'PointsSeeded'", DispatchSchema.Data, StringComparison.Ordinal);
        Assert.Contains("N'Motorová nafta'", DispatchSchema.Data, StringComparison.Ordinal);
        Assert.Contains("N'Benzín'", DispatchSchema.Data, StringComparison.Ordinal);
        Assert.Contains("UX_Disp_Distance_Pair", script, StringComparison.Ordinal);
        Assert.Contains("FK_Disp_Order_FuelType", script, StringComparison.Ordinal);
        Assert.Contains("FK_Disp_OrderStop_FuelType", script, StringComparison.Ordinal);
        Assert.Contains("NOT EXISTS (SELECT 1 FROM dbo.Disp_OrderStop", DispatchSchema.Data, StringComparison.Ordinal);
        Assert.Contains("WHERE v.RouteJson IS NULL", DispatchSchema.Data, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE", DispatchSchema.Data, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", DispatchSchema.Columns, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IF COL_LENGTH(N'dbo.Disp_SyncQueue', N'Warning') IS NULL", script, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE dbo.Disp_SyncQueue ADD Warning nvarchar(1000) NULL", script, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TRUNCATE", script, StringComparison.OrdinalIgnoreCase);
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
        var rates = await Assert.ThrowsAsync<DispatchStoreException>(() => store.GetRatesAsync(CancellationToken.None));

        Assert.Contains("ConnectionStrings:Dispatch", read.Message, StringComparison.Ordinal);
        Assert.Equal(read.Message, rates.Message);
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
