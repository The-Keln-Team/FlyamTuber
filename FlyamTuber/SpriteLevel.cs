using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace FlyamTuber;

/// <summary>
/// Один уровень громкости: картинка + пороги включения и выключения.
/// Самый первый уровень в списке — это "молчит", у него порог включения 0.
/// </summary>
public sealed class SpriteLevel
{
    /// <summary>Максимальная высота картинки в пикселях после загрузки.</summary>
    public const int MaxImageHeight = 1000;

    public string Name { get; set; } = "Уровень";

    public string? ImagePath { get; private set; }

    /// <summary>Готовая к отрисовке картинка. Заморожена, поэтому безопасна в любом потоке.</summary>
    public BitmapSource? Image { get; private set; }

    /// <summary>Громкость, с которой уровень включается.</summary>
    public double OnThreshold { get; set; }

    /// <summary>Громкость, ниже которой уровень отпускает. Должна быть МЕНЬШЕ OnThreshold.</summary>
    public double OffThreshold { get; set; }

    /// <summary>Трясти персонажа, пока активен этот уровень.</summary>
    public bool Shake { get; set; }

    public string FileName => ImagePath == null ? "нет картинки" : Path.GetFileName(ImagePath);

    /// <summary>Строка для списка в интерфейсе.</summary>
    public string Display
    {
        get
        {
            string thresholds = OnThreshold <= 0
                ? "от 0"
                : $"{OnThreshold:0} → {OffThreshold:0} %";
            string shake = Shake ? "  ~тряска~" : "";
            return $"{Name}    {thresholds}{shake}";
        }
    }

    /// <summary>
    /// Загружает PNG с диска. Большие рендеры уменьшаются до MaxImageHeight,
    /// иначе на каждом кадре мы будем зря таскать мегапиксели.
    /// </summary>
    public void LoadImage(string path)
    {
        // Первый проход: узнаём настоящий размер, не декодируя картинку целиком.
        int sourceHeight;
        using (var stream = File.OpenRead(path))
        {
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            sourceHeight = frame.PixelHeight;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(path);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;   // не держим файл открытым
        if (sourceHeight > MaxImageHeight)
            bitmap.DecodePixelHeight = MaxImageHeight;   // уменьшаем только если больше нужного
        bitmap.EndInit();
        bitmap.Freeze();                                 // дальше картинку можно читать из любого потока

        Image = bitmap;
        ImagePath = path;
    }
}