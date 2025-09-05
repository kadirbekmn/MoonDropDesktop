using System;                 // ← BUNU EKLEDİK
using System.Threading.Tasks;
using System.Windows;

namespace MoonDropDesktop
{
    public partial class App : Application
    {
        public App()
        {
            // UI thread'de yakalanmayan hatalar
            this.DispatcherUnhandledException += (s, e) =>
            {
                System.Windows.MessageBox.Show("Beklenmeyen hata:\n" + e.Exception);
                e.Handled = true;
            };

            // Task içindeki yakalanmayan hatalar
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                System.Windows.MessageBox.Show("Görev hatası:\n" + e.Exception);
                e.SetObserved();
            };

            // Process seviyesinde kalan hatalar
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                System.Windows.MessageBox.Show("Kritik hata:\n" + e.ExceptionObject);
            };
        }
    }
}
