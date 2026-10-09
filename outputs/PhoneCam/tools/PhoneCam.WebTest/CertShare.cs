using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System.Security.Cryptography.X509Certificates;

if (args.Length < 3) throw new ArgumentException("Podaj ścieżkę do CA, thumbprint certyfikatu serwera i stronę HTML.");
string certificatePath = Path.GetFullPath(args[0]);
string thumbprint = args[1].Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
string pagePath = Path.GetFullPath(args[2]);
foreach (string path in new[] { certificatePath, pagePath })
    if (!File.Exists(path)) throw new FileNotFoundException("Nie znaleziono pliku wymaganego przez serwer PhoneCam.", path);
using var certificateStore = new X509Store(StoreName.My, StoreLocation.CurrentUser);
certificateStore.Open(OpenFlags.ReadOnly);
var httpsCertificate = certificateStore.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false)
    .OfType<X509Certificate2>().FirstOrDefault() ?? throw new InvalidOperationException("Certyfikat TLS nie znajduje się w magazynie bieżącego użytkownika.");
if (!httpsCertificate.HasPrivateKey) throw new InvalidOperationException("Certyfikat TLS nie ma klucza prywatnego.");

var builder = WebApplication.CreateBuilder(args: []);
builder.WebHost.ConfigureKestrel(server =>
{
    server.ListenAnyIP(8787);
    server.ListenAnyIP(8443, listen => listen.UseHttps(httpsCertificate));
});
var app = builder.Build();
app.MapGet("/PhoneCam-RootCA.cer", () => Results.File(certificatePath, "application/pkix-cert", "PhoneCam-RootCA.cer"));
app.MapGet("/", () => Results.Content(File.ReadAllText(pagePath), "text/html; charset=utf-8"));
app.MapGet("/{slot}", (string slot) =>
    slot is "iphone" or "android"
        ? Results.Content(File.ReadAllText(pagePath), "text/html; charset=utf-8")
        : Results.NotFound());
await app.RunAsync();
