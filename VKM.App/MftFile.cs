using System.Collections.Generic;

namespace VKM.App
{
    // Bir çalıştırma aralığı (data run): diskte nerede, kaç küme
    public struct DataRun
    {
        public long Lcn;       // başlangıç küme numarası (Sparse ise anlamsız)
        public long Clusters;  // küme sayısı
        public bool Sparse;    // boşluk (veri yok, sıfır)
    }

    // MFT taramasında bulunan silinmiş bir dosya
    public class MftFile
    {
        public long Index;
        public string Name = "";
        public ulong Size;
        public bool Resident;              // veri MFT kaydının içinde mi
        public byte[]? ResidentData;       // resident ise ham veri
        public List<DataRun> Runs = new(); // non-resident ise aralıklar
        public long ParentIndex = 5;       // $FILE_NAME ebeveyn dizin kaydı
        public string FolderPath = "";     // yeniden kurulan klasör yolu ("" = kök, "[Lost&Found]" = ebeveyn kayıp)
        public bool Orphan;                // ebeveyni bulunamadı
    }

    // Tarama sonucu
    public class ScanResult
    {
        public List<MftFile> Files = new();
        public int ClusterSize = 4096;
        public string Info = "";
    }
}
