using AiCeo.Data;
using AiCeo.DependencyInjection;
using AiCeo.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.Storage;

namespace AiCeo.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder.UseMauiApp<App>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        // SQLite lives in the app's private data folder (%LOCALAPPDATA%\Packages\... or the
        // unpackaged equivalent). AICEO_DB_PATH overrides it.
        var databasePath = DatabasePaths.ResolveFromEnvironment(FileSystem.AppDataDirectory);

        builder.Services.AddAiCeoCore(databasePath);
        builder.Services.AddSingleton<IWorkspacePicker, LocalFolderPicker>();

        var app = builder.Build();

        // Create/upgrade the SQLite file before the first page renders.
        using (var scope = app.Services.CreateScope())
        {
            var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Startup");

            try
            {
                scope.ServiceProvider.InitializeDatabaseAsync(logger).GetAwaiter().GetResult();
                logger.LogInformation("Database ready at {Path}", databasePath);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Database initialisation failed ({Path})", databasePath);
            }
        }

        return app;
    }
}
