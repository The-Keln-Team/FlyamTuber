using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FlyamTuber;

// --- то, что попадает в JSON ---

public sealed class LevelData
{
    public string Name { get; set; } = "Уровень";
    public string? Image { get; set; }          // имя файла внутри папки профиля
    public double On { get; set; }
    public double Off { get; set; }
    public bool Shake { get; set; }
}

public sealed class BlinkData
{
    public bool Enabled { get; set; } = true;
    public string? Half { get; set; }
    public string? Closed { get; set; }
    public double EyeX { get; set; } = 0.34;
    public double EyeY { get; set; } = 0.16;
    public double EyeW { get; set; } = 0.32;
    public double EyeH { get; set; } = 0.12;
    public double MinSec { get; set; } = 3;
    public double MaxSec { get; set; } = 7;
    public int DurationMs { get; set; } = 170;
    public bool Sharp { get; set; }
}

public sealed class EffectsData
{
    public bool TransitionSmooth { get; set; } = true;
    public int TransitionMs { get; set; } = 120;

    public bool ShakeEnabled { get; set; } = true;
    public double ShakeAmplitude { get; set; } = 5;
    public double ShakeSpeed { get; set; } = 20;
    public double ShakeOn { get; set; } = 85;
    public double ShakeOff { get; set; } = 72;

    public bool BounceEnabled { get; set; } = true;
    public double BounceHeight { get; set; } = 18;
    public double BounceStiffness { get; set; } = 90;

    public bool TiltEnabled { get; set; }
    public double TiltDegrees { get; set; } = 3;
    public double TiltSpeed { get; set; } = 2.5;
}

public sealed class ProfileData
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Профиль 1";
    public bool Mirrored { get; set; }

    public List<LevelData> Levels { get; set; } = new();
    public BlinkData Blink { get; set; } = new();
    public EffectsData Effects { get; set; } = new();

    public string? MicDeviceId { get; set; }
    public double Gain { get; set; } = 1;

    /// <summary>Границы шкалы громкости — они зависят от микрофона, поэтому живут в профиле.</summary>
    public double MinDb { get; set; } = -55;
    public double MaxDb { get; set; } = -10;

    public override string ToString() => Name;   // для выпадающего списка
}

public sealed class AppState
{
    public List<ProfileData> Profiles { get; set; } = new();
    public string? ActiveProfileId { get; set; }

    /// <summary>Порт локального сервера для OBS — общий, а не на каждый профиль.</summary>
    public int OverlayPort { get; set; } = 8752;
    public bool OverlayEnabled { get; set; }
}

/// <summary>
/// Чтение и запись настроек. Файл лежит в AppData, картинки копируются
/// в папку профиля — иначе профиль сломается, как только PNG переедут.
/// </summary>
public static class ProfileStore
{
    public static string RootFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FlyamTuber");

    private static string StateFile => Path.Combine(RootFolder, "profiles.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    public static string ProfileFolder(string profileId)
        => Path.Combine(RootFolder, "profiles", profileId);

    /// <summary>
    /// Кладёт картинку в папку профиля и возвращает её имя.
    /// Если файл уже там — ничего не копирует.
    /// </summary>
    public static string? Adopt(string profileId, string? sourcePath)
    {
        if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) return null;

        string folder = ProfileFolder(profileId);
        Directory.CreateDirectory(folder);

        string name = Path.GetFileName(sourcePath);
        string target = Path.Combine(folder, name);

        if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(target),
                StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourcePath, target, true);
        }

        return name;
    }

    public static string? Resolve(string profileId, string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        string full = Path.Combine(ProfileFolder(profileId), fileName);
        return File.Exists(full) ? full : null;
    }

    public static AppState Load()
    {
        try
        {
            if (File.Exists(StateFile))
            {
                string json = File.ReadAllText(StateFile);
                var state = JsonSerializer.Deserialize<AppState>(json, Options);
                if (state != null && state.Profiles.Count > 0) return state;
            }
        }
        catch
        {
            // Битый файл не должен мешать запуску — просто начинаем с чистого профиля.
        }

        var fresh = new AppState();
        fresh.Profiles.Add(new ProfileData { Name = "Профиль 1" });
        fresh.ActiveProfileId = fresh.Profiles[0].Id;
        return fresh;
    }

    public static void Save(AppState state)
    {
        try
        {
            Directory.CreateDirectory(RootFolder);

            // Пишем во временный файл и подменяем: если свет погаснет
            // посреди записи, старые настройки останутся целыми.
            string temp = StateFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, Options));
            File.Move(temp, StateFile, true);
        }
        catch
        {
            // Не удалось сохранить — работать это не мешает.
        }
    }

    public static void DeleteProfileFolder(string profileId)
    {
        try
        {
            string folder = ProfileFolder(profileId);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
        catch
        {
        }
    }
}