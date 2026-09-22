using System.Windows;

namespace FakeSqlCompare;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeService.Initialize();
        base.OnStartup(e);
    }
}
