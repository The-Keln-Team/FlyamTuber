using System;
using System.Windows;
using System.Windows.Media.Imaging;

namespace FlyamTuber;

/// <summary>
/// Отвечает только за моргание. Не знает ни про громкость, ни про спрайты —
/// именно поэтому моргание не сбрасывает состояние персонажа, как требует ТЗ.
/// Выдаёт число Closedness от 0 (глаза открыты) до 1 (закрыты).
/// </summary>
public sealed class BlinkController
{
    public bool Enabled { get; set; } = true;

    /// <summary>Кадр с полузакрытыми глазами. Нужен для плавности, но не обязателен.</summary>
    public BitmapSource? HalfImage { get; private set; }
    public string? HalfPath { get; private set; }

    /// <summary>Кадр с закрытыми глазами.</summary>
    public BitmapSource? ClosedImage { get; private set; }
    public string? ClosedPath { get; private set; }

    /// <summary>Область глаз в долях картинки: 0..1 по ширине и высоте.</summary>
    public Rect EyeRegion { get; set; } = new Rect(0.34, 0.16, 0.32, 0.12);

    public double MinIntervalSec { get; set; } = 3.0;
    public double MaxIntervalSec { get; set; } = 7.0;
    public int DurationMs { get; set; } = 170;

    /// <summary>Резкая смена кадров вместо плавного перехода.</summary>
    public bool Sharp { get; set; }

    /// <summary>0 — глаза открыты, 1 — полностью закрыты.</summary>
    public double Closedness { get; private set; }

    public bool HasFrames => ClosedImage != null || HalfImage != null;

    private readonly Random _random = new();
    private double _waitSec;
    private double _blinkSec = -1;   // отрицательное значение = сейчас не моргаем

    public BlinkController() => ScheduleNext();

    public void LoadHalf(string path)
    {
        HalfImage = LoadFrame(path);
        HalfPath = path;
    }

    public void LoadClosed(string path)
    {
        ClosedImage = LoadFrame(path);
        ClosedPath = path;
    }

    /// <summary>Забывает загруженные кадры — нужно при переключении профиля.</summary>
    public void Clear()
    {
        HalfImage = null;
        HalfPath = null;
        ClosedImage = null;
        ClosedPath = null;
        Closedness = 0;
    }

    public void BlinkNow()
    {
        _blinkSec = 0;
    }

    /// <summary>Вызывается каждый кадр. dt — сколько секунд прошло с прошлого вызова.</summary>
    public void Update(double dt)
    {
        if (!Enabled || !HasFrames)
        {
            Closedness = 0;
            return;
        }

        double duration = Math.Max(0.04, DurationMs / 1000.0);

        if (_blinkSec >= 0)
        {
            _blinkSec += dt;
            double p = _blinkSec / duration;

            if (p >= 1.0)
            {
                _blinkSec = -1;
                Closedness = 0;
                ScheduleNext();
            }
            else
            {
                // Синус даёт мягкое закрытие и мягкое открытие: 0 -> 1 -> 0.
                // Именно это Флям делал руками кроссфейдом в DaVinci.
                Closedness = Sharp
                    ? (p > 0.15 && p < 0.85 ? 1.0 : 0.0)
                    : Math.Sin(Math.PI * p);
            }
            return;
        }

        _waitSec -= dt;
        if (_waitSec <= 0) _blinkSec = 0;
        Closedness = 0;
    }

    private void ScheduleNext()
    {
        double min = Math.Max(0.2, Math.Min(MinIntervalSec, MaxIntervalSec));
        double max = Math.Max(min, Math.Max(MinIntervalSec, MaxIntervalSec));
        _waitSec = min + _random.NextDouble() * (max - min);
    }

    private static BitmapSource LoadFrame(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(path);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelHeight = SpriteLevel.MaxImageHeight;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}