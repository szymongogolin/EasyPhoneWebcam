using System.IO;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PhoneCam.Desktop.Hosting;

/// <summary>
/// In-process Kestrel: plain HTTP serves only the root CA (the phone does not trust us yet),
/// HTTPS serves the publisher page for each slot.
/// </summary>
internal sealed class PhoneWebServer : IAsyncDisposable
{
    private WebApplication? _app;

    /// <param name="onOrientation">Called on a Kestrel thread with (slot, rotation in degrees counter-clockwise).</param>
    public async Task StartAsync(X509Certificate2 httpsCertificate, Action<string, int> onOrientation)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenAnyIP(Ports.CertificateDownload);
            kestrel.ListenAnyIP(Ports.PhonePage, listen => listen.UseHttps(httpsCertificate));
        });

        WebApplication app = builder.Build();
        app.MapGet("/PhoneCam-RootCA.cer", () =>
                Results.File(LocalCertificates.RootCaPath, "application/x-x509-ca-cert", "PhoneCam-RootCA.cer"))
            .RequireHost($"*:{Ports.CertificateDownload}");
        app.MapGet("/{slot}", (string slot, HttpResponse response) =>
            {
                if (!Slots.IsKnown(slot)) return Results.NotFound();
                response.Headers.CacheControl = "no-store";
                return Results.Content(File.ReadAllText(AppPaths.PhonePage), "text/html; charset=utf-8");
            })
            .RequireHost($"*:{Ports.PhonePage}");
        // The phone page reports how the PC must rotate its frames (see the orientation block in phonecam.html).
        app.MapPost("/api/orientation/{slot}", (string slot, OrientationReport report) =>
            {
                if (!Slots.IsKnown(slot) || report.Rotation is not (0 or 90 or 180 or 270)) return Results.BadRequest();
                onOrientation(slot, report.Rotation);
                return Results.NoContent();
            })
            .RequireHost($"*:{Ports.PhonePage}");

        await app.StartAsync();
        _app = app;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }
}

internal sealed record OrientationReport(int Rotation);
