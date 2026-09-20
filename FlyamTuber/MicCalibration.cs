using System;
using System.Collections.Generic;

namespace FlyamTuber;

/// <summary>
/// Слушает микрофон в три приёма — тишина, обычная речь, крик — и по ним
/// считает границы шкалы. Без этого пороги в процентах приходится подбирать
/// вручную, и у каждого микрофона они получаются свои.
/// </summary>
public sealed class MicCalibration
{
    public enum Phase { Idle, Silence, Speech, Scream, Done }

    private const double LeadInSec = 1.5;     // время на «приготовиться», не записывается

    public Phase Current { get; private set; } = Phase.Idle;

    /// <summary>Сколько секунд осталось в текущем шаге.</summary>
    public double Remaining { get; private set; }

    /// <summary>Идёт ли сейчас запись (а не отсчёт перед ней).</summary>
    public bool Recording { get; private set; }

    private readonly List<double> _silence = new();
    private readonly List<double> _speech = new();
    private readonly List<double> _scream = new();

    private double _elapsed;

    public string Title => Current switch
    {
        Phase.Silence => "Помолчите",
        Phase.Speech => "Говорите как обычно",
        Phase.Scream => "Крикните",
        _ => ""
    };

    public string Hint => Current switch
    {
        Phase.Silence => "Просто тишина — замеряю шум комнаты",
        Phase.Speech => "Читайте вслух что угодно обычным голосом",
        Phase.Scream => "Так громко, как будете орать на стриме",
        _ => ""
    };

    public void Start()
    {
        _silence.Clear();
        _speech.Clear();
        _scream.Clear();
        Current = Phase.Silence;
        _elapsed = 0;
        Recording = false;
    }

    public void Cancel() => Current = Phase.Idle;

    /// <summary>Возвращает true в тот кадр, когда калибровка закончилась.</summary>
    public bool Update(double dt, double rawDb)
    {
        if (Current is Phase.Idle or Phase.Done) return false;

        double total = LeadInSec + PhaseLength(Current);
        _elapsed += dt;

        Recording = _elapsed >= LeadInSec;
        Remaining = Math.Max(0, total - _elapsed);

        if (Recording && rawDb > -119)
        {
            Samples(Current).Add(rawDb);
        }

        if (_elapsed < total) return false;

        _elapsed = 0;
        Recording = false;

        Current = Current switch
        {
            Phase.Silence => Phase.Speech,
            Phase.Speech => Phase.Scream,
            _ => Phase.Done
        };

        return Current == Phase.Done;
    }

    private static double PhaseLength(Phase phase) => phase switch
    {
        Phase.Silence => 3.0,
        Phase.Speech => 6.0,
        Phase.Scream => 3.0,
        _ => 0
    };

    private List<double> Samples(Phase phase) => phase switch
    {
        Phase.Silence => _silence,
        Phase.Speech => _speech,
        _ => _scream
    };

    public bool HasEnoughData => _speech.Count > 30 && _scream.Count > 15;

    /// <summary>Итоги замера в децибелах.</summary>
    public readonly record struct Result(
        double MinDb, double MaxDb,
        double SpeechLowDb, double SpeechHighDb, double ScreamDb);

    public Result Build()
    {
        double noiseFloor = Percentile(_silence, 0.90, -70);

        // Тихие куски речи — это паузы между словами, они нам не нужны.
        double speechLow = Percentile(_speech, 0.35, -45);
        double speechHigh = Percentile(_speech, 0.95, -20);
        double scream = Percentile(_scream, 0.50, -12);

        // Низ шкалы ставим выше шума комнаты, но не выше тихой речи.
        double minDb = Math.Max(noiseFloor + 4, speechLow - 8);
        minDb = Math.Min(minDb, speechLow - 2);

        // Верх — по крику, с небольшим запасом, чтобы 100 % было достижимо.
        double maxDb = Math.Max(scream + 1, speechHigh + 4);

        if (maxDb - minDb < 12) maxDb = minDb + 12;   // страховка от вырожденной шкалы

        return new Result(minDb, maxDb, speechLow, speechHigh, scream);
    }

    private static double Percentile(List<double> values, double fraction, double fallback)
    {
        if (values.Count == 0) return fallback;

        var sorted = new List<double>(values);
        sorted.Sort();

        int index = (int)Math.Round(fraction * (sorted.Count - 1));
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}