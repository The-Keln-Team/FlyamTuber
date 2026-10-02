using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace FlyamTuber;

/// <summary>
/// Держит список уровней и решает, какой из них сейчас активен.
/// Это и есть ядро программы: громкость на входе — номер картинки на выходе.
/// </summary>
public sealed class LevelEngine
{
    public ObservableCollection<SpriteLevel> Levels { get; } = new();

    /// <summary>Минимальное время между переключениями, мс. Добивает остатки дребезга.</summary>
    public int MinHoldMs { get; set; } = 60;

    public int ActiveIndex { get; private set; } = -1;

    private long _lastSwitchTick;

    public SpriteLevel? Active =>
        ActiveIndex >= 0 && ActiveIndex < Levels.Count ? Levels[ActiveIndex] : null;

    /// <summary>Сбрасывает выбранный уровень — например при смене профиля.</summary>
    public void ResetActive() => ActiveIndex = -1;

    /// <summary>
    /// Главная функция. Вызывается на каждом кадре с текущей громкостью.
    /// </summary>
    public void Update(double volume)
    {
        if (Levels.Count == 0)
        {
            ActiveIndex = -1;
            return;
        }

        if (ActiveIndex < 0 || ActiveIndex >= Levels.Count)
        {
            ActiveIndex = 0;
            _lastSwitchTick = Environment.TickCount64;
            return;
        }

        // Кандидат — самый верхний уровень, чей порог включения пройден.
        // Порядок в списке и есть приоритет, отдельное поле приоритета не нужно.
        int target = 0;
        for (int i = 0; i < Levels.Count; i++)
        {
            if (volume >= Levels[i].OnThreshold) target = i;
        }

        if (target == ActiveIndex) return;

        // Гистерезис: вниз уходим только когда громкость упала ниже порога
        // ВЫКЛЮЧЕНИЯ текущего уровня, а не ниже порога включения.
        if (target < ActiveIndex && volume >= Levels[ActiveIndex].OffThreshold) return;

        // И не чаще, чем раз в MinHoldMs.
        if (Environment.TickCount64 - _lastSwitchTick < MinHoldMs) return;

        ActiveIndex = target;
        _lastSwitchTick = Environment.TickCount64;
    }

    /// <summary>
    /// Расставляет пороги поровну: нижний уровень всегда от нуля,
    /// остальные равномерно между 8 и 70 процентами. Дальше правится руками.
    /// </summary>
    public void AutoSpreadThresholds()
    {
        int count = Levels.Count;
        if (count == 0) return;

        Levels[0].OnThreshold = 0;
        Levels[0].OffThreshold = 0;

        if (count == 1) return;

        const double first = 8.0;
        const double last = 70.0;

        for (int i = 1; i < count; i++)
        {
            double t = (count == 2) ? first : first + (last - first) * (i - 1) / (count - 2);
            Levels[i].OnThreshold = Math.Round(t);
            Levels[i].OffThreshold = Math.Max(0, Math.Round(t) - 5);
        }
    }

    /// <summary>
    /// Создаёт набор уровней из выбранных файлов. Файлы сортируются по имени,
    /// поэтому 1.png … 5.png лягут от закрытого рта к открытому.
    /// </summary>
    public void LoadSet(string[] paths)
    {
        Levels.Clear();
        ActiveIndex = -1;

        var ordered = paths.OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);

        int index = 0;
        foreach (string path in ordered)
        {
            var level = new SpriteLevel
            {
                Name = index == 0 ? "Молчит" : $"Говорит {index}"
            };
            level.LoadImage(path);
            Levels.Add(level);
            index++;
        }

        AutoSpreadThresholds();
    }

    public SpriteLevel AddLevel()
    {
        var level = new SpriteLevel { Name = $"Говорит {Levels.Count}" };
        Levels.Add(level);
        AutoSpreadThresholds();
        return level;
    }

    public void RemoveLevel(SpriteLevel level)
    {
        Levels.Remove(level);
        if (ActiveIndex >= Levels.Count) ActiveIndex = Levels.Count - 1;
        AutoSpreadThresholds();
    }
    /// <summary>Переставляет уровень на новое место в списке.</summary>
    public void MoveLevel(int from, int to)
    {
        if (from == to) return;
        if (from < 0 || from >= Levels.Count) return;
        if (to < 0 || to >= Levels.Count) return;

        Levels.Move(from, to);
        ResetActive();
    }

    /// <summary>
    /// Возвращает пороги в порядок: самый тихий сверху. Вызывается после
    /// перетаскивания — картинки едут куда угодно, лестница громкости остаётся целой.
    /// </summary>
    public void NormalizeThresholds()
    {
        if (Levels.Count == 0) return;

        var on = Levels.Select(l => l.OnThreshold).OrderBy(v => v).ToArray();
        var off = Levels.Select(l => l.OffThreshold).OrderBy(v => v).ToArray();

        for (int i = 0; i < Levels.Count; i++)
        {
            Levels[i].OnThreshold = on[i];
            Levels[i].OffThreshold = off[i];
        }

        Levels[0].OnThreshold = 0;
        Levels[0].OffThreshold = 0;
    }
}