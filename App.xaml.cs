using System.IO;
using System.Windows;

namespace KineosEditor
{
    public partial class App : Application
    {
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            var window = new MainWindow();

            if (e.Args.Length > 0)
            {
                var file = e.Args[0];

                if (File.Exists(file))
                {
                    window.LoadFile(file);
                }
            }

            window.Show();
        }
    }
}
