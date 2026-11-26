using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Docnet.Core;
using Docnet.Core.Models;

namespace VKM.App
{
    // PDF'in ilk sayfasını resme çevirir (Docnet.Core / PDFium).
    public static class PdfPreview
    {
        public static BitmapSource? FirstPage(byte[] data, out string err)
        {
            err = "";
            try
            {
                // ~150 dpi A4 boyutuna sığacak şekilde ölçekler
                var dims = new PageDimensions(1240, 1754);
                using var reader = DocLib.Instance.GetDocReader(data, dims);
                if (reader.GetPageCount() < 1) { err = "sayfa yok"; return null; }

                using var page = reader.GetPageReader(0);
                int w = page.GetPageWidth();
                int h = page.GetPageHeight();
                byte[] raw = page.GetImage();   // BGRA, w*h*4

                var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, raw, w * 4);
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex) { err = ex.Message; return null; }
        }
    }
}
