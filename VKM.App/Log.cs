using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;

namespace VKM.App
{
    public enum LogLevel { Info, Ok, Warn, Bad }

    public class LogItem
    {
        public string Time { get; set; } = "";
        public string Text { get; set; } = "";
        public Brush Color { get; set; } = Brushes.Gainsboro;
    }

    // UFS tarzı alt günlük — her yerden Log.Info(...)/Ok/Warn/Bad çağrılır.
    public static class Log
    {
        public static readonly ObservableCollection<LogItem> Items = new();
        public static event Action? Added;
        const int Max = 800;

        public static void Info(string m) => Add(m, LogLevel.Info);
        public static void Ok(string m) => Add(m, LogLevel.Ok);
        public static void Warn(string m) => Add(m, LogLevel.Warn);
        public static void Bad(string m) => Add(m, LogLevel.Bad);

        static void Add(string msg, LogLevel lvl)
        {
            var app = Application.Current;
            if (app == null) return;
            app.Dispatcher.BeginInvoke(() =>
            {
                string key = lvl switch { LogLevel.Ok => "Ok", LogLevel.Warn => "Warn", LogLevel.Bad => "Bad", _ => "Text" };
                Brush br; try { br = Theme.Brush(key); } catch { br = Brushes.Gainsboro; }
                Items.Add(new LogItem { Time = DateTime.Now.ToString("HH:mm:ss"), Text = msg, Color = br });
                while (Items.Count > Max) Items.RemoveAt(0);
                Added?.Invoke();
            });
        }

        public static void Clear() => Application.Current?.Dispatcher.BeginInvoke(() => Items.Clear());
    }
}
