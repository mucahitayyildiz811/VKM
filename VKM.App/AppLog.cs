using System;
using System.Collections.Generic;

namespace VKM.App
{
    // Basit, zaman damgalı işlem logu (PC-3000 tarzı). Her modül buraya satır ekler.
    public static class AppLog
    {
        private static readonly List<string> _lines = new();
        private static readonly object _lock = new();

        public static event Action? Changed;

        public static void Add(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
            lock (_lock) { _lines.Add(line); if (_lines.Count > 5000) _lines.RemoveRange(0, 1000); }
            try { Changed?.Invoke(); } catch { }
        }

        public static string Text()
        {
            lock (_lock) return string.Join(Environment.NewLine, _lines);
        }

        public static void Clear()
        {
            lock (_lock) _lines.Clear();
            try { Changed?.Invoke(); } catch { }
        }
    }
}
