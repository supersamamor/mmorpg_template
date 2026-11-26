namespace Game.Admin;

/// <summary>The staff panel (docs/ADMIN_PANEL.md): settings come from appsettings.json, environment variables (<c>AdminPanel__Listen</c>) and the command line.</summary>
public static class Program
{
    /// <summary>Starts the panel; exits 1 with every problem when the settings are not usable.</summary>
    public static async Task<int> Main(string[] args)
    {
        WebApplication app;
        try
        {
            // The content root is the app's own folder, so appsettings.json (which keeps request addresses out of the log) is read wherever the panel is started from.
            app = PanelApp.Build(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        }
        catch (PanelSettingsException e)
        {
            await Console.Error.WriteLineAsync(e.Message);
            return 1;
        }
        await app.RunAsync();
        return 0;
    }
}
