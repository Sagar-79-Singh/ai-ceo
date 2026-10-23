namespace AiCeo.Maui.WinUI;

/// <summary>WinUI entry point: hosts the MAUI app inside a native Windows window.</summary>
public partial class App : MauiWinUIApplication
{
    public App()
    {
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
