using System;

namespace FlyamTuber;

/// <summary>Сдвиг и поворот персонажа на текущем кадре.</summary>
public struct CharacterTransform
{
    public double X;
    public double Y;
    public double Angle;
}

/// <summary>
/// Конвейер эффектов. Спрайт уже выбран по громкости — здесь мы только
/// двигаем и крутим то, что нарисовано. Сам PNG не меняется, как требует ТЗ.
/// Новый эффект добавляется сюда, а не правками по всему коду.
/// </summary>
public sealed class CharacterEffects
{
    // --- дрожание ---
    public bool ShakeEnabled { get; set; } = true;
    public double ShakeAmplitude { get; set; } = 5;    // пикселей
    public double ShakeSpeed { get; set; } = 20;       // смен в секунду

    /// <summary>Громкость, с которой начинается дрожание.</summary>
    public double ShakeOnThreshold { get; set; } = 85;

    /// <summary>Громкость, ниже которой дрожание прекращается. Меньше порога включения.</summary>
    public double ShakeOffThreshold { get; set; } = 72;

    /// <summary>Трясёт прямо сейчас.</summary>
    public bool IsShaking { get; private set; }

    // --- подпрыгивание в такт речи ---
    public bool BounceEnabled { get; set; } = true;
    public double BounceHeight { get; set; } = 18;     // пикселей на полной громкости
    public double BounceStiffness { get; set; } = 90;  // жёсткость пружины
    public double BounceDamping { get; set; } = 9;     // затухание

    // --- лёгкое покачивание ---
    public bool TiltEnabled { get; set; }
    public double TiltDegrees { get; set; } = 3;
    public double TiltSpeed { get; set; } = 2.5;

    public CharacterTransform Current { get; private set; }

    private readonly Random _random = new();
    private double _time;
    private double _shakeTimer;
    private double _shakeX;
    private double _shakeY;
    private double _bouncePosition;
    private double _bounceVelocity;

    /// <param name="forcedByLevel">Уровень с галочкой «трясти» — тряска включается независимо от порога.</param>
    public void Update(double dt, double volume, bool forcedByLevel)
    {
        _time += dt;
        double intensity = Math.Clamp(volume / 100.0, 0, 1);

        // У дрожания собственные пороги с гистерезисом, не привязанные к уровням.
        // Иначе громкий разговор и настоящий крик неразличимы.
        if (!ShakeEnabled)
        {
            IsShaking = false;
        }
        else if (forcedByLevel)
        {
            IsShaking = true;
        }
        else if (IsShaking)
        {
            if (volume < ShakeOffThreshold) IsShaking = false;
        }
        else
        {
            if (volume >= ShakeOnThreshold) IsShaking = true;
        }

        // Подпрыгивание сделано пружиной, а не прямым сдвигом: персонаж
        // слегка проскакивает цель и возвращается, отсюда живость.
        double target = BounceEnabled ? -BounceHeight * intensity : 0;
        _bounceVelocity += (target - _bouncePosition) * BounceStiffness * dt;
        _bounceVelocity -= _bounceVelocity * Math.Min(1.0, BounceDamping * dt);
        _bouncePosition += _bounceVelocity * dt;

        if (IsShaking)
        {
            // Новое случайное смещение не каждый кадр, а ShakeSpeed раз в секунду.
            // Иначе на 60 кадрах получается мелкая рябь вместо тряски.
            _shakeTimer -= dt;
            if (_shakeTimer <= 0)
            {
                _shakeTimer = 1.0 / Math.Max(1.0, ShakeSpeed);
                _shakeX = (_random.NextDouble() * 2 - 1) * ShakeAmplitude;
                _shakeY = (_random.NextDouble() * 2 - 1) * ShakeAmplitude;
            }
        }
        else
        {
            _shakeX = 0;
            _shakeY = 0;
            _shakeTimer = 0;
        }

        double angle = TiltEnabled
            ? TiltDegrees * Math.Sin(_time * TiltSpeed) * intensity
            : 0;

        Current = new CharacterTransform
        {
            X = _shakeX,
            Y = _bouncePosition + _shakeY,
            Angle = angle
        };
    }
}