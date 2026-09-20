using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace FlyamTuber;

public partial class MainWindow : Window
{
    private readonly AudioMeter _meter = new();
    private readonly LevelEngine _engine = new();
    private readonly BlinkController _blink = new();
    private readonly CharacterEffects _effects = new();
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private List<MMDevice> _devices = new();
    private int _shownIndex = -2;
    private double _lastSeconds;

    private int _transitionMs = 120;
    private bool _smoothTransition = true;
    private double _fadeSeconds = -1;      // отрицательное значение = переход не идёт

    private bool _mirrored;

    private readonly OverlayServer _overlay = new();
    private readonly MicCalibration _calibration = new();
    private int _fadeFromIndex = -1;

    private AppState _state = new();
    private ProfileData _profile = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _applying;          // идёт загрузка профиля — не перехватываем изменения

    private bool _pickingEyes;
    private Point _dragStart;
    private bool _dragging;

    public MainWindow()
    {
        InitializeComponent();

        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += OnTick;

        LevelsList.ItemsSource = _engine.Levels;

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };

        Loaded += OnLoaded;
        Closed += OnClosed;
    }


    // ---------- тёмная шапка окна ----------

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute,
                                                    ref int value, int size);

    private const int UseImmersiveDarkMode = 20;        // Windows 11 и поздние сборки 10
    private const int UseImmersiveDarkModeOld = 19;     // ранние сборки Windows 10

    /// <summary>
    /// Заголовок окна рисует сама Windows, средствами WPF его не перекрасить.
    /// Просим систему отдать окну тёмное оформление.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        try
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            int enabled = 1;

            if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, UseImmersiveDarkModeOld, ref enabled, sizeof(int));
        }
        catch
        {
            // Старая Windows — останется светлая шапка, работе не мешает.
        }
    }

    // ---------- запуск ----------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _devices = AudioMeter.GetInputDevices();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось получить список устройств: " + ex.Message;
            return;
        }

        if (_devices.Count == 0)
        {
            StatusText.Text = "Микрофоны не найдены.";
            return;
        }

        DeviceBox.ItemsSource = _devices;

        var preferred = AudioMeter.GetDefaultInputDevice();
        int index = 0;
        if (preferred != null)
        {
            for (int i = 0; i < _devices.Count; i++)
                if (_devices[i].ID == preferred.ID) { index = i; break; }
        }

        DeviceBox.SelectedIndex = index;

        _state = ProfileStore.Load();
        RefreshProfileList();

        OverlayPortBox.Text = _state.OverlayPort.ToString(CultureInfo.CurrentCulture);
        OverlayEnabledBox.IsChecked = _state.OverlayEnabled;
        if (_state.OverlayEnabled) StartOverlay();

        UpdateEyeRegionText();
        _lastSeconds = _clock.Elapsed.TotalSeconds;
        _timer.Start();
    }

    // ---------- главный цикл ----------

    private void OnTick(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Min(0.1, now - _lastSeconds);
        _lastSeconds = now;

        if (_calibration.Current is not (MicCalibration.Phase.Idle or MicCalibration.Phase.Done))
        {
            RunCalibration(dt);
        }

        double volume = _meter.Level;

        // 1. Какой уровень активен
        _engine.Update(volume);
        var active = _engine.Active;

        // 2. Эффекты и моргание — оба независимы от выбора спрайта
        _blink.Update(dt);
        _effects.Update(dt, volume, active != null && active.Shake);

        // 3. Индикатор
        if (MeterFill.Parent is FrameworkElement track)
            MeterFill.Width = Math.Max(0, track.ActualWidth) * volume / 100.0;
        LevelText.Text = $"{volume:0} %";
        MeterFill.Fill = new SolidColorBrush(active != null && active.Shake
            ? Color.FromRgb(0xFF, 0xA2, 0x6B)
            : Color.FromRgb(0xB7, 0x9B, 0xFF));

        // 4. Смена спрайта и переход
        if (_engine.ActiveIndex != _shownIndex)
        {
            StartTransition(active);
            _shownIndex = _engine.ActiveIndex;
        }
        AdvanceTransition(dt);

        // 5. Глаза поверх
        ApplyBlink();

        // 6. Сдвиг и поворот всего персонажа
        var transform = _effects.Current;
        CharTranslate.X = transform.X;
        CharTranslate.Y = transform.Y;
        CharRotate.Angle = transform.Angle;

        if (_overlay.IsRunning) PushOverlayState();

        StateText.Text = active == null ? "Состояние: —" : "Состояние: " + active.Name;
        StateSub.Text = active == null
            ? ""
            : $"{active.FileName}   громкость {volume:0} %"
              + (_effects.IsShaking ? "   тряска" : "");
    }




    // ---------- автокалибровка микрофона ----------

    private void Calibrate_Click(object sender, RoutedEventArgs e)
    {
        if (!_meter.IsRunning)
        {
            StatusText.Text = "Сначала выберите микрофон.";
            return;
        }

        _calibration.Start();
        CalibBar.Visibility = Visibility.Visible;
        CalibrateButton.IsEnabled = false;
    }

    private void CalibrateCancel_Click(object sender, RoutedEventArgs e)
    {
        _calibration.Cancel();
        CalibBar.Visibility = Visibility.Collapsed;
        CalibrateButton.IsEnabled = true;
        StatusText.Text = "Калибровка отменена.";
    }

    private void RunCalibration(double dt)
    {
        bool finished = _calibration.Update(dt, _meter.RawDb);

        CalibTitle.Text = _calibration.Title;
        CalibHint.Text = _calibration.Recording ? _calibration.Hint : "Приготовьтесь…";
        CalibCount.Text = Math.Ceiling(_calibration.Remaining).ToString("0", CultureInfo.InvariantCulture);

        if (!finished) return;

        CalibBar.Visibility = Visibility.Collapsed;
        CalibrateButton.IsEnabled = true;

        if (!_calibration.HasEnoughData)
        {
            StatusText.Text = "Слишком мало звука — проверьте, что микрофон слышит вас, и попробуйте снова.";
            return;
        }

        ApplyCalibration(_calibration.Build());
    }

    /// <summary>
    /// Превращает замеры в децибелах в границы шкалы и пороги уровней.
    /// Речь растягивается на все уровни, крик уходит в дрожание.
    /// </summary>
    private void ApplyCalibration(MicCalibration.Result result)
    {
        _meter.MinDb = result.MinDb;
        _meter.MaxDb = result.MaxDb;

        double ToPercent(double db) =>
            Math.Clamp((db - result.MinDb) / (result.MaxDb - result.MinDb) * 100.0, 0, 100);

        double low = ToPercent(result.SpeechLowDb);
        double high = ToPercent(result.SpeechHighDb);
        if (high - low < 15) high = Math.Min(100, low + 15);

        int count = _engine.Levels.Count;
        if (count > 1)
        {
            _engine.Levels[0].OnThreshold = 0;
            _engine.Levels[0].OffThreshold = 0;

            for (int i = 1; i < count; i++)
            {
                double t = count == 2 ? low : low + (high - low) * (i - 1) / (count - 2);
                _engine.Levels[i].OnThreshold = Math.Round(t);
                _engine.Levels[i].OffThreshold = Math.Max(0, Math.Round(t) - 5);
            }
        }

        double scream = ToPercent(result.ScreamDb);
        _effects.ShakeOnThreshold = Math.Round(Math.Clamp(scream - 3, high + 4, 99));
        _effects.ShakeOffThreshold = Math.Max(0, _effects.ShakeOnThreshold - 12);

        ShakeOnBox.Text = _effects.ShakeOnThreshold.ToString("0", CultureInfo.CurrentCulture);
        ShakeOffBox.Text = _effects.ShakeOffThreshold.ToString("0", CultureInfo.CurrentCulture);

        LevelsList.Items.Refresh();
        ShowSelectedLevel();
        SaveSoon();

        StatusText.Text = $"Готово. Шкала {result.MinDb:0} … {result.MaxDb:0} дБ, "
                        + $"речь {low:0}–{high:0} %, дрожание от {_effects.ShakeOnThreshold:0} %.";
    }

    // ---------- вывод для OBS ----------

    private void StartOverlay()
    {
        try
        {
            _overlay.Start(_state.OverlayPort);
            UpdateOverlaySources();
            OverlayUrlText.Text = _overlay.Url;
            StatusText.Text = "Вывод включён. Адрес скопируйте кнопкой и вставьте в источник «Браузер».";
        }
        catch (Exception ex)
        {
            OverlayEnabledBox.IsChecked = false;
            _state.OverlayEnabled = false;
            OverlayUrlText.Text = "не запустился";
            StatusText.Text = "Не удалось занять порт " + _state.OverlayPort
                            + ": " + ex.Message + ". Попробуйте другой номер.";
        }
    }

    private void OverlayToggle_Click(object sender, RoutedEventArgs e)
    {
        _state.OverlayEnabled = OverlayEnabledBox.IsChecked == true;

        if (_state.OverlayEnabled)
        {
            StartOverlay();
        }
        else
        {
            _overlay.Stop();
            OverlayUrlText.Text = "выключено";
        }

        SaveSoon();
    }

    private void OverlayPort_LostFocus(object sender, RoutedEventArgs e)
    {
        int port = (int)ParseNumber(OverlayPortBox.Text, _state.OverlayPort, 1024, 65535);
        OverlayPortBox.Text = port.ToString(CultureInfo.CurrentCulture);

        if (port == _state.OverlayPort) return;
        _state.OverlayPort = port;

        if (_overlay.IsRunning) StartOverlay();   // перезапуск на новом порту
        SaveSoon();
    }

    private void CopyOverlayUrl_Click(object sender, RoutedEventArgs e)
    {
        if (!_overlay.IsRunning)
        {
            StatusText.Text = "Сначала включите вывод.";
            return;
        }

        try
        {
            Clipboard.SetText(_overlay.Url);
            StatusText.Text = "Адрес скопирован: " + _overlay.Url;
        }
        catch
        {
            StatusText.Text = "Скопируйте вручную: " + _overlay.Url;
        }
    }

    /// <summary>Сообщает серверу, какие файлы отдавать браузеру.</summary>
    private void UpdateOverlaySources()
    {
        if (!_overlay.IsRunning) return;

        var paths = new List<string>();
        foreach (SpriteLevel level in _engine.Levels)
            paths.Add(level.ImagePath ?? "");

        _overlay.SetSources(paths, _blink.HalfPath, _blink.ClosedPath);
    }

    /// <summary>
    /// Отдаёт странице уже посчитанные числа. Вся логика остаётся здесь,
    /// в браузере только присваивание прозрачностей — поэтому картинка
    /// в OBS и в предпросмотре гарантированно одинаковая.
    /// </summary>
    private void PushOverlayState()
    {
        Rect eye = _blink.EyeRegion;
        var c = CultureInfo.InvariantCulture;
        var t = _effects.Current;

        _overlay.PushState(
            "{\"v\":" + _overlay.Version
            + ",\"i\":" + _shownIndex.ToString(c)
            + ",\"p\":" + _fadeFromIndex.ToString(c)
            + ",\"o\":" + PreviewImage.Opacity.ToString("0.###", c)
            + ",\"h\":" + BlinkHalf.Opacity.ToString("0.###", c)
            + ",\"c\":" + BlinkClosed.Opacity.ToString("0.###", c)
            + ",\"x\":" + t.X.ToString("0.##", c)
            + ",\"y\":" + t.Y.ToString("0.##", c)
            + ",\"a\":" + t.Angle.ToString("0.###", c)
            + ",\"m\":" + (_mirrored ? "true" : "false")
            + ",\"ex\":" + eye.X.ToString("0.####", c)
            + ",\"ey\":" + eye.Y.ToString("0.####", c)
            + ",\"ew\":" + eye.Width.ToString("0.####", c)
            + ",\"eh\":" + eye.Height.ToString("0.####", c)
            + "}");
    }

    // ---------- профили ----------

    private void RefreshProfileList()
    {
        _applying = true;

        ProfileBox.ItemsSource = null;
        ProfileBox.ItemsSource = _state.Profiles;

        var active = _state.Profiles.Find(p => p.Id == _state.ActiveProfileId)
                     ?? _state.Profiles[0];
        ProfileBox.SelectedItem = active;

        _applying = false;
        ApplyProfile(active);
    }

    private void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applying) return;
        if (ProfileBox.SelectedItem is not ProfileData chosen) return;
        if (chosen.Id == _profile.Id) return;

        SaveNow();                      // не теряем правки в предыдущем профиле
        _state.ActiveProfileId = chosen.Id;
        ApplyProfile(chosen);
        SaveNow();
    }

    /// <summary>Разворачивает профиль в живые объекты и в поля интерфейса.</summary>
    private void ApplyProfile(ProfileData profile)
    {
        _applying = true;
        _profile = profile;

        // уровни
        _engine.Levels.Clear();
        _engine.ResetActive();
        int missing = 0;

        foreach (LevelData data in profile.Levels)
        {
            var level = new SpriteLevel
            {
                Name = data.Name,
                OnThreshold = data.On,
                OffThreshold = data.Off,
                Shake = data.Shake
            };

            string? path = ProfileStore.Resolve(profile.Id, data.Image);
            if (path != null)
            {
                try { level.LoadImage(path); } catch { missing++; }
            }
            else if (data.Image != null) missing++;

            _engine.Levels.Add(level);
        }

        // моргание
        _blink.Enabled = profile.Blink.Enabled;
        _blink.MinIntervalSec = profile.Blink.MinSec;
        _blink.MaxIntervalSec = profile.Blink.MaxSec;
        _blink.DurationMs = profile.Blink.DurationMs;
        _blink.Sharp = profile.Blink.Sharp;
        _blink.EyeRegion = new Rect(profile.Blink.EyeX, profile.Blink.EyeY,
                                    profile.Blink.EyeW, profile.Blink.EyeH);
        _blink.Clear();

        string? halfPath = ProfileStore.Resolve(profile.Id, profile.Blink.Half);
        if (halfPath != null) { try { _blink.LoadHalf(halfPath); } catch { missing++; } }

        string? closedPath = ProfileStore.Resolve(profile.Id, profile.Blink.Closed);
        if (closedPath != null) { try { _blink.LoadClosed(closedPath); } catch { missing++; } }

        // эффекты
        EffectsData fx = profile.Effects;
        _smoothTransition = fx.TransitionSmooth;
        _transitionMs = fx.TransitionMs;
        _effects.ShakeEnabled = fx.ShakeEnabled;
        _effects.ShakeAmplitude = fx.ShakeAmplitude;
        _effects.ShakeSpeed = fx.ShakeSpeed;
        _effects.ShakeOnThreshold = fx.ShakeOn;
        _effects.ShakeOffThreshold = fx.ShakeOff;
        _effects.BounceEnabled = fx.BounceEnabled;
        _effects.BounceHeight = fx.BounceHeight;
        _effects.BounceStiffness = fx.BounceStiffness;
        _effects.TiltEnabled = fx.TiltEnabled;
        _effects.TiltDegrees = fx.TiltDegrees;
        _effects.TiltSpeed = fx.TiltSpeed;

        // зеркало
        _mirrored = profile.Mirrored;
        MirrorBox.IsChecked = _mirrored;
        CharScale.ScaleX = _mirrored ? -1 : 1;

        // микрофон
        _meter.Gain = profile.Gain;
        _meter.MinDb = profile.MinDb;
        _meter.MaxDb = profile.MaxDb;
        GainSlider.Value = Math.Clamp(profile.Gain, GainSlider.Minimum, GainSlider.Maximum);

        if (profile.MicDeviceId != null)
        {
            int index = _devices.FindIndex(d => d.ID == profile.MicDeviceId);
            if (index >= 0) DeviceBox.SelectedIndex = index;
        }

        // интерфейс
        BlinkEnabledBox.IsChecked = _blink.Enabled;
        SharpBlinkBox.IsChecked = _blink.Sharp;
        BlinkMinBox.Text = _blink.MinIntervalSec.ToString("0.#", CultureInfo.CurrentCulture);
        BlinkMaxBox.Text = _blink.MaxIntervalSec.ToString("0.#", CultureInfo.CurrentCulture);
        BlinkDurBox.Text = _blink.DurationMs.ToString(CultureInfo.CurrentCulture);
        HalfFileText.Text = profile.Blink.Half ?? "не загружен";
        ClosedFileText.Text = profile.Blink.Closed ?? "не загружен";

        SharpTransitionBox.IsChecked = !_smoothTransition;
        TransitionMsBox.Text = _transitionMs.ToString(CultureInfo.CurrentCulture);
        ShakeEnabledBox.IsChecked = _effects.ShakeEnabled;
        ShakeAmpBox.Text = _effects.ShakeAmplitude.ToString("0.#", CultureInfo.CurrentCulture);
        ShakeSpeedBox.Text = _effects.ShakeSpeed.ToString("0.#", CultureInfo.CurrentCulture);
        ShakeOnBox.Text = _effects.ShakeOnThreshold.ToString("0", CultureInfo.CurrentCulture);
        ShakeOffBox.Text = _effects.ShakeOffThreshold.ToString("0", CultureInfo.CurrentCulture);
        BounceEnabledBox.IsChecked = _effects.BounceEnabled;
        BounceHeightBox.Text = _effects.BounceHeight.ToString("0.#", CultureInfo.CurrentCulture);
        BounceStiffBox.Text = _effects.BounceStiffness.ToString("0.#", CultureInfo.CurrentCulture);
        TiltEnabledBox.IsChecked = _effects.TiltEnabled;
        TiltDegBox.Text = _effects.TiltDegrees.ToString("0.#", CultureInfo.CurrentCulture);
        TiltSpeedBox.Text = _effects.TiltSpeed.ToString("0.#", CultureInfo.CurrentCulture);

        _shownIndex = -2;
        PreviewImage.Source = null;
        PreviewImageOld.Source = null;

        LevelsList.Items.Refresh();
        if (_engine.Levels.Count > 0) LevelsList.SelectedIndex = 0;

        UpdateEyeRegionText();
        UpdateEyeClip();

        UpdateOverlaySources();

        StatusText.Text = missing > 0
            ? $"Профиль «{profile.Name}» загружен, но {missing} картинок не найдено."
            : $"Профиль «{profile.Name}» загружен.";

        _applying = false;
    }

    /// <summary>Собирает текущее состояние обратно в профиль.</summary>
    private void CaptureProfile()
    {
        if (_applying) return;

        _profile.Mirrored = _mirrored;

        _profile.Levels.Clear();
        foreach (SpriteLevel level in _engine.Levels)
        {
            _profile.Levels.Add(new LevelData
            {
                Name = level.Name,
                Image = ProfileStore.Adopt(_profile.Id, level.ImagePath),
                On = level.OnThreshold,
                Off = level.OffThreshold,
                Shake = level.Shake
            });
        }

        Rect eye = _blink.EyeRegion;
        _profile.Blink = new BlinkData
        {
            Enabled = _blink.Enabled,
            Half = ProfileStore.Adopt(_profile.Id, _blink.HalfPath),
            Closed = ProfileStore.Adopt(_profile.Id, _blink.ClosedPath),
            EyeX = eye.X,
            EyeY = eye.Y,
            EyeW = eye.Width,
            EyeH = eye.Height,
            MinSec = _blink.MinIntervalSec,
            MaxSec = _blink.MaxIntervalSec,
            DurationMs = _blink.DurationMs,
            Sharp = _blink.Sharp
        };

        _profile.Effects = new EffectsData
        {
            TransitionSmooth = _smoothTransition,
            TransitionMs = _transitionMs,
            ShakeEnabled = _effects.ShakeEnabled,
            ShakeAmplitude = _effects.ShakeAmplitude,
            ShakeSpeed = _effects.ShakeSpeed,
            ShakeOn = _effects.ShakeOnThreshold,
            ShakeOff = _effects.ShakeOffThreshold,
            BounceEnabled = _effects.BounceEnabled,
            BounceHeight = _effects.BounceHeight,
            BounceStiffness = _effects.BounceStiffness,
            TiltEnabled = _effects.TiltEnabled,
            TiltDegrees = _effects.TiltDegrees,
            TiltSpeed = _effects.TiltSpeed
        };

        _profile.Gain = _meter.Gain;
        _profile.MinDb = _meter.MinDb;
        _profile.MaxDb = _meter.MaxDb;
        if (DeviceBox.SelectedItem is MMDevice device) _profile.MicDeviceId = device.ID;
    }

    /// <summary>Отложенное сохранение: пока настройки крутят, на диск не пишем.</summary>
    private void SaveSoon()
    {
        if (_applying) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        if (_applying) return;
        _saveTimer.Stop();
        CaptureProfile();
        ProfileStore.Save(_state);
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        SaveNow();

        var created = new ProfileData { Name = $"Профиль {_state.Profiles.Count + 1}" };
        _state.Profiles.Add(created);
        _state.ActiveProfileId = created.Id;

        RefreshProfileList();
        SaveNow();
    }

    private void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        string name = AskForText("Название профиля", _profile.Name);
        if (string.IsNullOrWhiteSpace(name)) return;

        _profile.Name = name.Trim();
        int index = ProfileBox.SelectedIndex;
        ProfileBox.ItemsSource = null;
        ProfileBox.ItemsSource = _state.Profiles;
        ProfileBox.SelectedIndex = index;
        SaveNow();
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_state.Profiles.Count <= 1)
        {
            StatusText.Text = "Последний профиль удалить нельзя.";
            return;
        }

        var answer = MessageBox.Show(this,
            $"Удалить профиль «{_profile.Name}» вместе с его картинками?",
            "FlyamTuber", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        string removedId = _profile.Id;
        _state.Profiles.RemoveAll(p => p.Id == removedId);
        ProfileStore.DeleteProfileFolder(removedId);
        _state.ActiveProfileId = _state.Profiles[0].Id;

        RefreshProfileList();
        SaveNow();
    }

    /// <summary>Маленькое окно ввода — в WPF своего нет.</summary>
    private string AskForText(string title, string initial)
    {
        var box = new TextBox
        {
            Text = initial,
            Margin = new Thickness(0, 0, 0, 12),
            Background = new SolidColorBrush(Color.FromRgb(0x1D, 0x1C, 0x23)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF0, 0xEE)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x37, 0x45)),
            Padding = new Thickness(6, 4, 6, 4)
        };

        var ok = new Button { Content = "OK", Width = 90, IsDefault = true };
        var cancel = new Button { Content = "Отмена", Width = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = title,
            Content = panel,
            Width = 340,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x13, 0x19))
        };

        ok.Click += (_, _) => { dialog.DialogResult = true; };
        box.Focus();
        box.SelectAll();

        return dialog.ShowDialog() == true ? box.Text : "";
    }

    // ---------- плавный переход между спрайтами ----------

    private void StartTransition(SpriteLevel? next)
    {
        var incoming = next?.Image;

        if (!_smoothTransition || _transitionMs <= 0 || PreviewImage.Source == null)
        {
            PreviewImageOld.Source = null;
            PreviewImageOld.Opacity = 0;
            PreviewImage.Source = incoming;
            PreviewImage.Opacity = 1;
            _fadeFromIndex = -1;
            _fadeSeconds = -1;
        }
        else
        {
            // Старый кадр уходит вниз и остаётся непрозрачным, новый проявляется
            // поверх него. Так между кадрами не мелькает пустота.
            PreviewImageOld.Source = PreviewImage.Source;
            PreviewImageOld.Opacity = 1;
            PreviewImage.Source = incoming;
            PreviewImage.Opacity = 0;
            _fadeFromIndex = _shownIndex;
            _fadeSeconds = 0;
        }

        PreviewHint.Visibility = _engine.Levels.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void AdvanceTransition(double dt)
    {
        if (_fadeSeconds < 0) return;

        _fadeSeconds += dt;
        double progress = _fadeSeconds / Math.Max(0.02, _transitionMs / 1000.0);

        if (progress >= 1.0)
        {
            PreviewImage.Opacity = 1;
            PreviewImageOld.Opacity = 0;
            PreviewImageOld.Source = null;
            _fadeFromIndex = -1;
            _fadeSeconds = -1;
        }
        else
        {
            PreviewImage.Opacity = progress;
        }
    }

    // ---------- моргание ----------

    private void ApplyBlink()
    {
        double c = _blink.Closedness;

        if (!_blink.HasFrames)
        {
            BlinkHalf.Opacity = 0;
            BlinkClosed.Opacity = 0;
            return;
        }

        double half, closed;

        if (_blink.HalfImage == null)
        {
            half = 0;
            closed = c;
        }
        else if (_blink.ClosedImage == null)
        {
            half = c;
            closed = 0;
        }
        else if (c <= 0.5)
        {
            half = c * 2.0;
            closed = 0;
        }
        else
        {
            half = 1.0;
            closed = (c - 0.5) * 2.0;
        }

        if (BlinkHalf.Source != _blink.HalfImage) BlinkHalf.Source = _blink.HalfImage;
        if (BlinkClosed.Source != _blink.ClosedImage) BlinkClosed.Source = _blink.ClosedImage;

        BlinkHalf.Opacity = half;
        BlinkClosed.Opacity = closed;
    }

    // ---------- настройки эффектов ----------

    private void SharpTransition_Click(object sender, RoutedEventArgs e)
    {
        _smoothTransition = SharpTransitionBox.IsChecked != true;
        SaveSoon();
    }

    private void Mirror_Click(object sender, RoutedEventArgs e)
    {
        _mirrored = MirrorBox.IsChecked == true;
        CharScale.ScaleX = _mirrored ? -1 : 1;
        UpdateEyeClip();
        SaveSoon();
    }

    private void EffectToggle_Click(object sender, RoutedEventArgs e)
    {
        _effects.ShakeEnabled = ShakeEnabledBox.IsChecked == true;
        _effects.BounceEnabled = BounceEnabledBox.IsChecked == true;
        _effects.TiltEnabled = TiltEnabledBox.IsChecked == true;
        SaveSoon();
    }

    private void EffectField_LostFocus(object sender, RoutedEventArgs e)
    {
        _transitionMs = (int)ParseNumber(TransitionMsBox.Text, _transitionMs, 0, 2000);

        _effects.ShakeAmplitude = ParseNumber(ShakeAmpBox.Text, _effects.ShakeAmplitude, 0, 80);
        _effects.ShakeSpeed = ParseNumber(ShakeSpeedBox.Text, _effects.ShakeSpeed, 1, 60);
        _effects.ShakeOnThreshold = ParseNumber(ShakeOnBox.Text, _effects.ShakeOnThreshold, 1, 100);
        _effects.ShakeOffThreshold = ParseNumber(ShakeOffBox.Text, _effects.ShakeOffThreshold, 0, 100);
        if (_effects.ShakeOffThreshold >= _effects.ShakeOnThreshold)
            _effects.ShakeOffThreshold = Math.Max(0, _effects.ShakeOnThreshold - 10);

        _effects.BounceHeight = ParseNumber(BounceHeightBox.Text, _effects.BounceHeight, 0, 200);
        _effects.BounceStiffness = ParseNumber(BounceStiffBox.Text, _effects.BounceStiffness, 10, 400);

        _effects.TiltDegrees = ParseNumber(TiltDegBox.Text, _effects.TiltDegrees, 0, 30);
        _effects.TiltSpeed = ParseNumber(TiltSpeedBox.Text, _effects.TiltSpeed, 0.1, 20);

        TransitionMsBox.Text = _transitionMs.ToString(CultureInfo.CurrentCulture);
        ShakeAmpBox.Text = _effects.ShakeAmplitude.ToString("0.#", CultureInfo.CurrentCulture);
        ShakeSpeedBox.Text = _effects.ShakeSpeed.ToString("0.#", CultureInfo.CurrentCulture);
        ShakeOnBox.Text = _effects.ShakeOnThreshold.ToString("0", CultureInfo.CurrentCulture);
        ShakeOffBox.Text = _effects.ShakeOffThreshold.ToString("0", CultureInfo.CurrentCulture);
        BounceHeightBox.Text = _effects.BounceHeight.ToString("0.#", CultureInfo.CurrentCulture);
        BounceStiffBox.Text = _effects.BounceStiffness.ToString("0.#", CultureInfo.CurrentCulture);
        TiltDegBox.Text = _effects.TiltDegrees.ToString("0.#", CultureInfo.CurrentCulture);
        TiltSpeedBox.Text = _effects.TiltSpeed.ToString("0.#", CultureInfo.CurrentCulture);
        SaveSoon();
    }

    // ---------- область глаз ----------

    /// <summary>Размер картинки на экране. Одинаков для обеих систем координат.</summary>
    private bool TryGetImageSize(double boxWidth, double boxHeight, out double width, out double height)
    {
        width = height = 0;

        var source = (PreviewImage.Source as BitmapSource) ?? _blink.ClosedImage ?? _blink.HalfImage;
        if (source == null || boxWidth <= 0 || boxHeight <= 0) return false;

        double scale = Math.Min(boxWidth / source.PixelWidth, boxHeight / source.PixelHeight);
        width = source.PixelWidth * scale;
        height = source.PixelHeight * scale;
        return true;
    }

    /// <summary>
    /// Прямоугольник картинки в координатах холста. Именно здесь была ошибка:
    /// элемент Image при Stretch="Uniform" ужимается до самой картинки и центрируется
    /// внутри ячейки, поэтому холст и картинка начинаются в разных точках.
    /// </summary>
    private bool TryGetImageRectOnCanvas(out Rect rect)
    {
        rect = default;
        double cw = EyeCanvas.ActualWidth;
        double ch = EyeCanvas.ActualHeight;
        if (!TryGetImageSize(cw, ch, out double dw, out double dh)) return false;

        rect = new Rect((cw - dw) / 2, (ch - dh) / 2, dw, dh);
        return true;
    }

    /// <summary>Прямоугольник картинки внутри самого элемента Image — для Clip.</summary>
    private bool TryGetImageRectLocal(out Rect rect)
    {
        rect = default;
        double cw = PreviewImage.ActualWidth;
        double ch = PreviewImage.ActualHeight;
        if (!TryGetImageSize(cw, ch, out double dw, out double dh)) return false;

        rect = new Rect((cw - dw) / 2, (ch - dh) / 2, dw, dh);
        return true;
    }

    private void UpdateEyeClip()
    {
        if (!TryGetImageRectLocal(out Rect local) || !TryGetImageRectOnCanvas(out Rect onCanvas))
        {
            BlinkHalf.Clip = null;
            BlinkClosed.Clip = null;
            EyeRect.Visibility = Visibility.Collapsed;
            return;
        }

        Rect region = _blink.EyeRegion;

        // Клип живёт внутри зеркалимого слоя, поэтому берёт координаты как есть.
        var clip = new RectangleGeometry(new Rect(
            local.X + region.X * local.Width,
            local.Y + region.Y * local.Height,
            region.Width * local.Width,
            region.Height * local.Height));
        clip.Freeze();
        BlinkHalf.Clip = clip;
        BlinkClosed.Clip = clip;

        // Рамка лежит поверх слоя и не зеркалится сама — отражаем вручную.
        double rx = _mirrored ? 1 - region.X - region.Width : region.X;
        Canvas.SetLeft(EyeRect, onCanvas.X + rx * onCanvas.Width);
        Canvas.SetTop(EyeRect, onCanvas.Y + region.Y * onCanvas.Height);
        EyeRect.Width = Math.Max(0, region.Width * onCanvas.Width);
        EyeRect.Height = Math.Max(0, region.Height * onCanvas.Height);

        EyeRect.Visibility = (ShowEyeRectBox.IsChecked == true || _pickingEyes)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void PreviewArea_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateEyeClip();

    private void SetEyeRegion_Click(object sender, RoutedEventArgs e)
    {
        _pickingEyes = !_pickingEyes;
        EyeHintBar.Visibility = _pickingEyes ? Visibility.Visible : Visibility.Collapsed;
        PreviewArea.Cursor = _pickingEyes ? Cursors.Cross : null;
        SetEyeButton.Content = _pickingEyes ? "Отмена" : "Задать областью";
        UpdateEyeClip();
    }

    private void PreviewArea_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_pickingEyes) return;
        if (!TryGetImageRectOnCanvas(out _)) return;

        _dragging = true;
        _dragStart = e.GetPosition(EyeCanvas);
        PreviewArea.CaptureMouse();
    }

    private void PreviewArea_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        Point current = e.GetPosition(EyeCanvas);
        var box = new Rect(_dragStart, current);

        Canvas.SetLeft(EyeRect, box.X);
        Canvas.SetTop(EyeRect, box.Y);
        EyeRect.Width = box.Width;
        EyeRect.Height = box.Height;
        EyeRect.Visibility = Visibility.Visible;
    }

    private void PreviewArea_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        PreviewArea.ReleaseMouseCapture();

        Point end = e.GetPosition(EyeCanvas);
        var box = new Rect(_dragStart, end);

        if (box.Width < 6 || box.Height < 6)
        {
            StatusText.Text = "Область слишком маленькая — обведите глаза ещё раз.";
            UpdateEyeClip();
            return;
        }

        if (!TryGetImageRectOnCanvas(out Rect image)) return;

        double x = Math.Clamp((box.X - image.X) / image.Width, 0, 1);
        double y = Math.Clamp((box.Y - image.Y) / image.Height, 0, 1);
        double w = Math.Clamp(box.Width / image.Width, 0.01, 1 - x);
        double h = Math.Clamp(box.Height / image.Height, 0.01, 1 - y);

        // Рисовали по зеркальному персонажу — возвращаем координаты к исходной картинке.
        if (_mirrored) x = Math.Clamp(1 - x - w, 0, 1);

        _blink.EyeRegion = new Rect(x, y, w, h);

        _pickingEyes = false;
        EyeHintBar.Visibility = Visibility.Collapsed;
        PreviewArea.Cursor = null;
        SetEyeButton.Content = "Задать областью";

        UpdateEyeRegionText();
        UpdateEyeClip();
        _blink.BlinkNow();
        SaveSoon();
    }

    private void UpdateEyeRegionText()
    {
        Rect r = _blink.EyeRegion;
        EyeRegionText.Text = $"x {r.X:0.00}  y {r.Y:0.00}  ш {r.Width:0.00}  в {r.Height:0.00}";
    }

    private void ShowEyeRect_Click(object sender, RoutedEventArgs e) => UpdateEyeClip();

    // ---------- настройки моргания ----------

    private void BlinkEnabled_Click(object sender, RoutedEventArgs e)
    {
        _blink.Enabled = BlinkEnabledBox.IsChecked == true;
        SaveSoon();
    }

    private void SharpBlink_Click(object sender, RoutedEventArgs e)
    {
        _blink.Sharp = SharpBlinkBox.IsChecked == true;
        SaveSoon();
    }

    private void BlinkNow_Click(object sender, RoutedEventArgs e) => _blink.BlinkNow();

    private void BlinkField_LostFocus(object sender, RoutedEventArgs e)
    {
        _blink.MinIntervalSec = ParseNumber(BlinkMinBox.Text, _blink.MinIntervalSec, 0.2, 60);
        _blink.MaxIntervalSec = ParseNumber(BlinkMaxBox.Text, _blink.MaxIntervalSec, 0.2, 60);
        _blink.DurationMs = (int)ParseNumber(BlinkDurBox.Text, _blink.DurationMs, 40, 2000);

        BlinkMinBox.Text = _blink.MinIntervalSec.ToString("0.#", CultureInfo.CurrentCulture);
        BlinkMaxBox.Text = _blink.MaxIntervalSec.ToString("0.#", CultureInfo.CurrentCulture);
        BlinkDurBox.Text = _blink.DurationMs.ToString(CultureInfo.CurrentCulture);
        SaveSoon();
    }

    private void PickHalf_Click(object sender, RoutedEventArgs e)
    {
        string? path = AskForImage("Кадр с полузакрытыми глазами");
        if (path == null) return;

        try { _blink.LoadHalf(path); }
        catch (Exception ex) { ShowLoadError(ex); return; }

        HalfFileText.Text = System.IO.Path.GetFileName(path);
        UpdateEyeClip();
        _blink.BlinkNow();
        SaveSoon();
        UpdateOverlaySources();
    }

    private void PickClosed_Click(object sender, RoutedEventArgs e)
    {
        string? path = AskForImage("Кадр с закрытыми глазами");
        if (path == null) return;

        try { _blink.LoadClosed(path); }
        catch (Exception ex) { ShowLoadError(ex); return; }

        ClosedFileText.Text = System.IO.Path.GetFileName(path);
        UpdateEyeClip();
        _blink.BlinkNow();
        SaveSoon();
        UpdateOverlaySources();
    }

    private string? AskForImage(string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "Изображения PNG (*.png)|*.png|Все файлы (*.*)|*.*"
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void ShowLoadError(Exception ex)
        => MessageBox.Show(this, "Не удалось загрузить картинку: " + ex.Message,
            "FlyamTuber", MessageBoxButton.OK, MessageBoxImage.Warning);

    // ---------- уровни ----------

    private void LoadSet_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите все кадры персонажа сразу",
            Filter = "Изображения PNG (*.png)|*.png|Все файлы (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true) return;

        try { _engine.LoadSet(dialog.FileNames); }
        catch (Exception ex) { ShowLoadError(ex); return; }

        _shownIndex = -2;
        PreviewImage.Source = null;
        PreviewImageOld.Source = null;
        LevelsList.Items.Refresh();
        if (_engine.Levels.Count > 0) LevelsList.SelectedIndex = 0;
        UpdateEyeClip();
        StatusText.Text = $"Загружено уровней: {_engine.Levels.Count}.";
        SaveSoon();
        UpdateOverlaySources();
    }

    private void AddLevel_Click(object sender, RoutedEventArgs e)
    {
        var level = _engine.AddLevel();
        LevelsList.Items.Refresh();
        LevelsList.SelectedItem = level;
        SaveSoon();
    }

    private void RemoveLevel_Click(object sender, RoutedEventArgs e)
    {
        if (LevelsList.SelectedItem is not SpriteLevel level) return;
        _engine.RemoveLevel(level);
        _shownIndex = -2;
        LevelsList.Items.Refresh();
        SaveSoon();
    }

    private void AutoSpread_Click(object sender, RoutedEventArgs e)
    {
        _engine.AutoSpreadThresholds();
        LevelsList.Items.Refresh();
        ShowSelectedLevel();
        SaveSoon();
    }

    private void LevelsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ShowSelectedLevel();

    private void ShowSelectedLevel()
    {
        if (LevelsList.SelectedItem is not SpriteLevel level)
        {
            NameBox.Text = "";
            OnBox.Text = "";
            OffBox.Text = "";
            FileText.Text = "—";
            ShakeBox.IsChecked = false;
            return;
        }

        NameBox.Text = level.Name;
        OnBox.Text = level.OnThreshold.ToString("0", CultureInfo.InvariantCulture);
        OffBox.Text = level.OffThreshold.ToString("0", CultureInfo.InvariantCulture);
        ShakeBox.IsChecked = level.Shake;
        FileText.Text = level.FileName;
    }

    private void LevelField_LostFocus(object sender, RoutedEventArgs e)
    {
        if (LevelsList.SelectedItem is not SpriteLevel level) return;

        if (!string.IsNullOrWhiteSpace(NameBox.Text))
            level.Name = NameBox.Text.Trim();

        level.OnThreshold = ParseNumber(OnBox.Text, level.OnThreshold, 0, 100);
        level.OffThreshold = ParseNumber(OffBox.Text, level.OffThreshold, 0, 100);

        if (_engine.Levels.Count > 0 && ReferenceEquals(_engine.Levels[0], level))
        {
            level.OnThreshold = 0;
            level.OffThreshold = 0;
        }
        else if (level.OffThreshold >= level.OnThreshold)
        {
            level.OffThreshold = Math.Max(0, level.OnThreshold - 5);
            StatusText.Text = "Порог выключения должен быть ниже порога включения — поправил на "
                            + $"{level.OffThreshold:0} %.";
        }

        LevelsList.Items.Refresh();
        ShowSelectedLevel();
        SaveSoon();
    }

    private static double ParseNumber(string text, double fallback, double min, double max)
    {
        text = text.Replace('.', ',').Trim();
        if (double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out double value))
            return Math.Clamp(value, min, max);
        return fallback;
    }

    private void ShakeBox_Click(object sender, RoutedEventArgs e)
    {
        if (LevelsList.SelectedItem is not SpriteLevel level) return;
        level.Shake = ShakeBox.IsChecked == true;
        LevelsList.Items.Refresh();
        SaveSoon();
    }

    private void PickImage_Click(object sender, RoutedEventArgs e)
    {
        if (LevelsList.SelectedItem is not SpriteLevel level)
        {
            StatusText.Text = "Сначала выберите уровень в списке слева.";
            return;
        }

        string? path = AskForImage("Картинка для уровня «" + level.Name + "»");
        if (path == null) return;

        try { level.LoadImage(path); }
        catch (Exception ex) { ShowLoadError(ex); return; }

        _shownIndex = -2;
        FileText.Text = level.FileName;
        LevelsList.Items.Refresh();
        SaveSoon();
        UpdateOverlaySources();
    }

    // ---------- микрофон ----------

    private void DeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceBox.SelectedItem is not MMDevice device) return;

        try
        {
            _meter.Start(device);
            StatusText.Text = "Слушаю: " + device.FriendlyName;
            SaveSoon();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось открыть устройство: " + ex.Message;
        }
    }

    private void GainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GainLabel == null) return;
        _meter.Gain = e.NewValue;
        GainLabel.Text = $"Чувствительность — {e.NewValue:0.0}x";
        SaveSoon();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SaveNow();
        _timer.Stop();
        _overlay.Dispose();
        _meter.Dispose();
    }
}