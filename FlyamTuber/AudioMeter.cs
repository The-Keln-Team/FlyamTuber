using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FlyamTuber;

/// <summary>
/// Слушает выбранный микрофон и держит текущий уровень громкости в процентах (0..100).
/// Класс ничего не знает про окно и про спрайты — он только про звук.
/// </summary>
public sealed class AudioMeter : IDisposable
{
    private WasapiCapture? _capture;
    private double _level;              // сглаженный уровень, 0..100
    private double _rawDb = -120;       // последняя измеренная громкость в дБ
    private readonly object _lock = new();

    /// <summary>Усиление входного сигнала. 1.0 — как есть, 2.0 — вдвое громче.</summary>
    public double Gain { get; set; } = 1.0;

    /// <summary>Как быстро уровень растёт (0..1). Больше — резче реакция на голос.</summary>
    public double Attack { get; set; } = 0.55;

    /// <summary>Как быстро уровень падает (0..1). Меньше — плавнее затухание.</summary>
    public double Release { get; set; } = 0.25;

    /// <summary>Всё тише этого порога считаем тишиной. Убирает шум комнаты и кулеров.</summary>
    public double NoiseGate { get; set; } = 2.0;

    /// <summary>Нижняя граница шкалы в децибелах — тише этого считаем нулём.</summary>
    public double MinDb { get; set; } = -55.0;

    /// <summary>Верхняя граница в децибелах — громче этого считаем сотней.</summary>
    public double MaxDb { get; set; } = -10.0;

    /// <summary>Сырая громкость в децибелах, без шкалы и сглаживания. Нужна калибровке.</summary>
    public double RawDb
    {
        get { lock (_lock) { return _rawDb; } }
    }

    /// <summary>Текущий уровень, 0..100. Читать можно из любого потока.</summary>
    public double Level
    {
        get { lock (_lock) { return _level; } }
    }

    public bool IsRunning => _capture != null;

    /// <summary>Все активные устройства записи в системе.</summary>
    public static List<MMDevice> GetInputDevices()
    {
        var enumerator = new MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .ToList();
    }

    /// <summary>Микрофон, выбранный в системе по умолчанию (может отсутствовать).</summary>
    public static MMDevice? GetDefaultInputDevice()
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }
        catch
        {
            return null;
        }
    }

    public void Start(MMDevice device)
    {
        Stop();

        // true = событийная синхронизация, 20 = размер буфера в миллисекундах.
        // Чем меньше буфер, тем меньше задержка реакции на голос.
        var capture = new WasapiCapture(device, true, 20);
        capture.DataAvailable += OnDataAvailable;
        capture.StartRecording();
        _capture = capture;
    }

    public void Stop()
    {
        if (_capture == null) return;

        _capture.DataAvailable -= OnDataAvailable;
        try { _capture.StopRecording(); } catch { /* устройство уже отвалилось */ }
        _capture.Dispose();
        _capture = null;

        lock (_lock) { _level = 0; _rawDb = -120; }
    }

    /// <summary>
    /// Вызывается самой NAudio в фоновом потоке каждые ~20 мс с куском звука.
    /// Здесь считаем RMS — среднюю "энергию" куска, это честная громкость,
    /// в отличие от пикового значения, которое дёргается от каждого щелчка.
    /// </summary>
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var format = _capture?.WaveFormat;
        if (format == null || e.BytesRecorded == 0) return;

        double sumOfSquares = 0;
        int sampleCount = 0;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            // Обычный формат WASAPI: 32-битные float в диапазоне -1..+1
            for (int i = 0; i + 3 < e.BytesRecorded; i += 4)
            {
                float sample = BitConverter.ToSingle(e.Buffer, i);
                sumOfSquares += sample * sample;
                sampleCount++;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            // Запасной вариант: 16-битный PCM
            for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                float sample = BitConverter.ToInt16(e.Buffer, i) / 32768f;
                sumOfSquares += sample * sample;
                sampleCount++;
            }
        }
        else
        {
            return; // неизвестный формат — молча игнорируем
        }

        if (sampleCount == 0) return;

        double rms = Math.Sqrt(sumOfSquares / sampleCount) * Gain;

        // Переводим в децибелы и растягиваем диапазон -60..0 дБ на шкалу 0..100.
        // Линейная шкала для голоса не годится: речь живёт в самом низу диапазона
        // и на линейной шкале индикатор почти не шевелится.
        double db = 20.0 * Math.Log10(Math.Max(rms, 1e-7));
        double target = Math.Clamp((db - MinDb) / (MaxDb - MinDb) * 100.0, 0.0, 100.0);

        if (target < NoiseGate) target = 0;

        lock (_lock)
        {
            _rawDb = db;

            // Сглаживание: вверх летим быстро, вниз опускаемся медленно.
            // Без этого спрайты будут мигать даже при ровной речи.
            double factor = target > _level ? Attack : Release;
            _level += (target - _level) * factor;
            if (_level < 0.05) _level = 0;
        }
    }

    public void Dispose() => Stop();
}