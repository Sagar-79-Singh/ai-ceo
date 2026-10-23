using Microsoft.Maui.Controls;

namespace AiCeo.Maui;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage())
        {
            Title = "AI CEO Orchestrator",
            Width = 1440,
            Height = 940,
            MinimumWidth = 980,
            MinimumHeight = 640
        };

        return window;
    }
}
