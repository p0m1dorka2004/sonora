using System.Windows;
using vkr.Themes;

namespace vkr
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            ThemeManager.Initialize();
            base.OnStartup(e);
        }
    }
}
