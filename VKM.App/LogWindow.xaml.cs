using System;
using System.Windows;

namespace VKM.App
{
    public partial class LogWindow : Window
    {
        public LogWindow()
        {
            InitializeComponent();
            Refresh();
            AppLog.Changed += OnChanged;
            Closed += (_, __) => AppLog.Changed -= OnChanged;
        }

        private void OnChanged()
        {
            try { Dispatcher.Invoke(Refresh); } catch { }
        }

        private void Refresh()
        {
            LogBox.Text = AppLog.Text();
            LogBox.ScrollToEnd();
        }

        private void Clear_Click(object sender, RoutedEventArgs e) { AppLog.Clear(); Refresh(); }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(AppLog.Text()); } catch { }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
