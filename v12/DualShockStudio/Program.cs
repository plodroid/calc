using DualShockStudio.Models;
using DualShockStudio.Services;

namespace DualShockStudio;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var settings = SettingsStore.Load();
        using var controller = new DS4Controller();
        using var audio = new AudioBeatService();
        Application.Run(new MainForm(controller, audio, settings));
    }
}
