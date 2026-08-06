using System.Diagnostics.CodeAnalysis;
using Aware.Application;
using Aware.Infrastructure;
using Aware.Platform;
using Uno.Resizetizer;

namespace Aware;

public partial class App : Microsoft.UI.Xaml.Application
{
    public App()
    {
        this.InitializeComponent();
    }

    protected Window? MainWindow { get; private set; }
    protected IHost? Host { get; private set; }

    [SuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "Uno.Extensions APIs are used in a way that is safe for trimming in this template context.")]
    protected async override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var builder = this.CreateBuilder(args)
            .UseToolkitNavigation()
            .Configure(host => host
#if DEBUG
                .UseEnvironment(Environments.Development)
#endif
                .UseLogging(configure: (context, logBuilder) =>
                {
                    logBuilder
                        .SetMinimumLevel(
                            context.HostingEnvironment.IsDevelopment()
                                ? LogLevel.Information
                                : LogLevel.Warning)
                        .CoreLogLevel(LogLevel.Warning);
                }, enableUnoLogging: true)
                .UseConfiguration(configure: configBuilder =>
                    configBuilder
                        .EmbeddedSource<App>()
                        .Section<AppConfig>()
                )
                .UseLocalization()
                .ConfigureServices((context, services) =>
                {
                    // Application ports.
                    services.AddSingleton<IRoomRepository, JsonRoomRepository>();
                    services.AddSingleton<IRoomRecognitionService, SimulatedRoomRecognitionService>();
                    services.AddSingleton<IRenderSnapshotFactory, RenderSnapshotFactory>();
                    services.AddSingleton<IObjectActionResolver, ObjectActionResolver>();
                    services.AddSingleton<IPrivacyService, PrivacyService>();

                    // Platform adapters. Swapping the capture tier is a single
                    // registration change; the experience above it is unchanged.
                    services.AddSingleton<ISpatialCaptureAdapter, SimulationCaptureAdapter>();
                    services.AddSingleton<IHapticsService, HapticsService>();
                    services.AddSingleton<IMotionSettings, MotionSettings>();

                    services.AddTransient<SpatialRoomViewModel>();
                })
                .UseNavigation(RegisterRoutes)
            );

        MainWindow = builder.Window;

#if DEBUG
        MainWindow.UseStudio();
#endif
        MainWindow.SetWindowIcon();

        Host = await builder.NavigateAsync<Shell>();
    }

    private static void RegisterRoutes(IViewRegistry views, IRouteRegistry routes)
    {
        views.Register(
            new ViewMap(ViewModel: typeof(ShellModel)),
            new ViewMap<SpatialRoomPage, SpatialRoomViewModel>()
        );

        // Navigation exists for room-level destinations only; lenses and
        // selection are in-page state (09-UNO-NOTES).
        routes.Register(
            new RouteMap("", View: views.FindByViewModel<ShellModel>(),
                Nested:
                [
                    new RouteMap("Room", View: views.FindByViewModel<SpatialRoomViewModel>(), IsDefault: true),
                ]
            )
        );
    }
}
