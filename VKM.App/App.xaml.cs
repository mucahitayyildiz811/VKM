using System.Windows;

namespace VKM.App
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            Theme.Init();     // açık/koyu tema kaynaklarını yükle (varsayılan açık, seçim hatırlanır)
            base.OnStartup(e);
        }
    }
}
