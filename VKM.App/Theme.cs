using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace VKM.App
{
    // Uygulama geneli açık/koyu tema. Renkler Application kaynaklarında DynamicResource olarak durur;
    // tema değişince aynı anahtarların rengi değişir → tüm pencereler canlı güncellenir.
    // Seçim diske kaydedilir (bir dahaki açılışta hatırlanır). Varsayılan: AÇIK.
    public static class Theme
    {
        public static bool IsDark { get; private set; }
        public static event Action? Changed;

        static readonly string CfgPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VKM", "tema.txt");

        // anahtar → (açık renk, koyu renk)
        static readonly Dictionary<string, (string light, string dark)> P = new()
        {
            ["Bg"]        = ("#ffffff", "#0f151c"),   // pencere zemini
            ["Panel"]     = ("#f4f6f8", "#141b24"),   // yan paneller
            ["Head"]      = ("#eef1f4", "#1a222d"),   // üst şeritler, başlıklar
            ["Card"]      = ("#ffffff", "#161c25"),   // disk kartı, kutu
            ["Card2"]     = ("#eef2f6", "#11161d"),   // iç kart (bölüm)
            ["Line"]      = ("#e2e7ec", "#28323f"),   // kenarlık / ayraç
            ["Hover"]     = ("#eef4fb", "#1b2531"),
            ["Btn"]       = ("#ffffff", "#1a2330"),   // ikincil buton zemini
            ["Text"]      = ("#1d2733", "#dde4ec"),   // ana yazı
            ["Text2"]     = ("#3a4654", "#c3ccd6"),   // ikincil koyu yazı
            ["Dim"]       = ("#6b7785", "#8593a2"),   // soluk yazı
            ["Accent"]    = ("#0d9488", "#0d9488"),   // turkuaz vurgu
            ["AccentText"]= ("#0f8f86", "#5eead4"),
            ["Sel"]       = ("#cfe4ff", "#17324e"),   // seçili satır
            ["SelBg"]     = ("#d7f0ec", "#14312e"),   // seçili modül/vurgu zemini
            ["Ok"]        = ("#2e9e52", "#4ade80"),
            ["Warn"]      = ("#c98a00", "#f1c447"),
            ["Bad"]       = ("#d2433a", "#f0726a"),
            ["Purple"]    = ("#6d28d9", "#8b5cf6"),
            ["ConsoleBg"] = ("#0b1118", "#05080b"),   // log/konsol — iki temada da koyu
            ["ConsoleFg"] = ("#59e07e", "#59e07e"),   // konsol yeşil yazı
            ["ConsoleTx"] = ("#cdd6e1", "#cdd6e1"),   // konsol açık gri yazı (iki temada da açık)
            ["OkBg"]      = ("#e3f6ea", "#15372b"),
            ["WarnBg"]    = ("#fdf3dd", "#4a3a1a"),
            ["BadBg"]     = ("#fdecea", "#3a2430"),
            ["Purple"]    = ("#6d28d9", "#a78bfa"),
            ["PurpleBg"]  = ("#efe9fb", "#2a2140"),
            ["Link"]      = ("#1d4ed8", "#8fd0ff"),
            ["ShieldBg"]  = ("#e3f6ea", "#13351f"),   // "yazma koruması" rozeti
            ["ShieldLn"]  = ("#7fd3a0", "#1f7a46"),
            ["ShieldTx"]  = ("#1f9e55", "#4ade80"),
            ["Led"]       = ("#11c0ad", "#2bd8c4"),   // seçili modülde koşan turkuaz ışık
        };

        public static Color Col(string key) => (Color)ColorConverter.ConvertFromString(
            P.TryGetValue(key, out var v) ? (IsDark ? v.dark : v.light) : "#ff00ff");

        public static SolidColorBrush Brush(string key)
        {
            var b = new SolidColorBrush(Col(key)); b.Freeze(); return b;
        }

        public static void Init()
        {
            bool dark = false;
            try { if (File.Exists(CfgPath)) dark = File.ReadAllText(CfgPath).Trim() == "dark"; } catch { }
            Apply(dark);
        }

        public static void Toggle() => Apply(!IsDark);

        public static void Apply(bool dark)
        {
            IsDark = dark;
            var res = Application.Current?.Resources;
            if (res == null) return;
            foreach (var kv in P)
            {
                var c = (Color)ColorConverter.ConvertFromString(dark ? kv.Value.dark : kv.Value.light);
                var br = new SolidColorBrush(c); br.Freeze();
                res["Br" + kv.Key] = br;          // {DynamicResource BrBg}, {DynamicResource BrText} ...
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CfgPath)!);
                File.WriteAllText(CfgPath, dark ? "dark" : "light");
            }
            catch { }
            Changed?.Invoke();
        }
    }
}
