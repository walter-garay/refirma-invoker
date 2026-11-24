using System.Net;
using System.Windows.Forms;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Serilog;
using SignerBridge.Core.Abstractions;
using SignerBridge.Core.Configuration;
using SignerBridge.Core.Jobs;
using SignerBridge.Core.Security;
using SignerBridge.Core.Storage;
using SignerBridge.Host.Api;
using SignerBridge.Host.Security;
using SignerBridge.Host.SystemIntegration;
using SignerBridge.Host.Tray;
using SignerBridge.Providers.ReFirmaPdf;

namespace SignerBridge.Host;

public partial class Program
{
    private const string SingleInstanceMutexName = @"Local\SignerBridge.Desktop.SingleInstance";

    [STAThread]
    public static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        bool headless = args.Contains("--headless", StringComparer.OrdinalIgnoreCase);
        bool mockProvider = args.Contains("--mock", StringComparer.OrdinalIgnoreCase);
        bool launchedViaUri = args.Any(a => a.StartsWith($"{UriProtocolRegistrar.Scheme}://", StringComparison.OrdinalIgnoreCase));

        bool createdNew = true;
        using var singleInstanceMutex = headless ? null : new Mutex(true, SingleInstanceMutexName, out createdNew);
        if (singleInstanceMutex is not null && !createdNew)
        {
            if (!launchedViaUri)
            {
                MessageBox.Show(
                    "SignerBridge ya se encuentra ejecutándose en la bandeja del sistema.",
                    "SignerBridge",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            return;
        }

        if (!headless)
            UriProtocolRegistrar.EnsureRegistered();

        var app = BuildWebApp(args, mockProvider);
        var opts = app.Services.GetRequiredService<IOptions<SignerBridgeOptions>>().Value;
        var tokenStore = app.Services.GetRequiredService<DpapiTokenStore>();
        tokenStore.GetOrCreateToken();

        string logsDir = Path.Combine(opts.DataDirectory, "logs");
        string endpointUrl = $"http://{opts.Host}:{opts.Port}";

        try
        {
            app.StartAsync().GetAwaiter().GetResult();
            Log.Information("SignerBridge iniciado en {Endpoint} (Headless={Headless}, Mock={Mock})", endpointUrl, headless, mockProvider);

            if (headless)
            {
                app.WaitForShutdownAsync().GetAwaiter().GetResult();
            }
            else
            {
                using var tray = new TrayApplicationContext(
                    endpointUrl,
                    tokenStore,
                    logsDir,
                    async () => await app.StopAsync());
                Application.Run(tray);
            }
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    public static WebApplication BuildWebApp(string[] args, bool mockProvider = false)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.Configure<SignerBridgeOptions>(
            builder.Configuration.GetSection(SignerBridgeOptions.SectionName));

        var tempOpts = builder.Configuration
            .GetSection(SignerBridgeOptions.SectionName)
            .Get<SignerBridgeOptions>() ?? new SignerBridgeOptions();

        string logsDir = Path.Combine(tempOpts.DataDirectory, "logs");
        Directory.CreateDirectory(logsDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logsDir, "signerbridge-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        builder.Host.UseSerilog();

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = tempOpts.MaxUploadBytes;
            var ip = IPAddress.TryParse(tempOpts.Host, out var parsed) ? parsed : IPAddress.Loopback;
            kestrel.Listen(ip, tempOpts.Port);
        });

        builder.Services.Configure<FormOptions>(form =>
        {
            form.MultipartBodyLengthLimit = tempOpts.MaxUploadBytes;
        });

        builder.Services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<SignerBridgeOptions>>().Value;
            return new DpapiTokenStore(o.DataDirectory);
        });

        builder.Services.AddSingleton<DesktopSessionMonitor>();
        builder.Services.AddSingleton<IArchiveService, ArchiveService>();
        builder.Services.AddSingleton<ISignerProvider>(sp =>
            new ReFirmaPdfProvider(
                sp.GetRequiredService<IOptions<SignerBridgeOptions>>(),
                sp.GetService<ILogger<ReFirmaPdfProvider>>(),
                forceMock: mockProvider ? true : null));
        builder.Services.AddSingleton<ISignerProviderFactory, SignerProviderFactory>();
        builder.Services.AddSingleton<JobManager>();

        var app = builder.Build();
        app.UseWebSockets();
        app.UseMiddleware<OriginAndTokenMiddleware>();
        app.MapSignerApi();

        return app;
    }
}
