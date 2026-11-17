using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOIRAI.Analysis;
using MOIRAI.Persistence;
using MOIRAI.Scheduling;

namespace MOIRAI.ViewModels;

public sealed partial class AnimationViewModel : ViewModelBase
{
    private readonly IRepository _repo;
    private readonly DispatcherTimer _timer;
    private const double Fps = 60;

    public AnimationViewModel(IRepository repo)
    {
        _repo = repo;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / Fps) };
        _timer.Tick += OnTick;
    }

    [ObservableProperty] private GanttData? _data;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoRun))]
    private bool _hasRun;

    public bool NoRun => !HasRun;
    [ObservableProperty] private string _hint = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClockText))]
    private double _currentBlock;

    [ObservableProperty] private double _maxBlock;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _loop;

    [ObservableProperty] private double _speed = 2;

    private int _bpd = 3;
    private DateOnly _start;

    private Scenario? _snapshot;
    private Schedule? _deterministic;
    private int _mcSeed;
    private bool _suppressReload;

    public ObservableCollection<RunOption> RunOptions { get; } = new();

    [ObservableProperty] private RunOption? _selectedRun;

    [ObservableProperty] private bool _showRunSelector;

    partial void OnSelectedRunChanged(RunOption? value)
    {
        if (!_suppressReload && HasRun) LoadSelectedRun();
    }

    public string ClockText
    {
        get
        {
            if (!HasRun) return string.Empty;
            int blk = Math.Clamp((int)CurrentBlock, 0, (int)Math.Max(0, MaxBlock));
            int dayIdx = blk / _bpd, inDay = blk % _bpd;
            var date = _start.AddDays(dayIdx);
            return $"{BlockName(inDay, _bpd)} · {date:ddd dd MMM yyyy} · Day {dayIdx + 1}";
        }
    }

    private static string BlockName(int inDay, int bpd)
    {
        if (bpd == 3) return inDay switch { 0 => "Morning", 1 => "Midday", _ => "Afternoon" };
        return $"Block {inDay + 1}/{bpd}";
    }

    public override void OnNavigatedTo()
    {
        Pause();
        var sc = _repo.GetScenario();
        if (sc.RunSnapshot is { } snap && sc.RunSchedule is { } sch)
        {
            _snapshot = snap;
            _deterministic = sch;
            _bpd = Math.Max(1, snap.Config.BlocksPerDay);
            _start = snap.HorizonStart;
            int days = Math.Max(1, snap.HorizonEnd.DayNumber - snap.HorizonStart.DayNumber + 1);
            MaxBlock = days * _bpd - 1;
            HasRun = true;
            Hint = string.Empty;
            BuildRunOptions(sc);
            LoadSelectedRun();
        }
        else
        {
            _snapshot = null;
            _deterministic = null;
            Data = null;
            HasRun = false;
            RunOptions.Clear();
            ShowRunSelector = false;
            Hint = $"Run \"{sc.Name}\" on the Planning page to view playback.";
            OnPropertyChanged(nameof(ClockText));
        }
    }

    private void BuildRunOptions(Scenario sc)
    {
        _suppressReload = true;
        RunOptions.Clear();
        RunOptions.Add(new RunOption("Deterministic plan", -1, true));
        if (sc.McReplay is { } mc)
        {
            _mcSeed = mc.Seed;
            if (mc.FinishP10Run >= 0) RunOptions.Add(new RunOption("P10 Simulation", mc.FinishP10Run, false));
            if (mc.FinishP50Run >= 0) RunOptions.Add(new RunOption("P50 Simulation", mc.FinishP50Run, false));
            if (mc.FinishP90Run >= 0) RunOptions.Add(new RunOption("P90 Simulation", mc.FinishP90Run, false));
        }
        ShowRunSelector = RunOptions.Count > 1;
        SelectedRun = RunOptions[0];
        _suppressReload = false;
    }

    private void LoadSelectedRun()
    {
        if (_snapshot is null || _deterministic is null) return;
        Pause();

        Scenario scenario = _snapshot;
        Schedule schedule = _deterministic;
        if (SelectedRun is { IsDeterministic: false } opt)
        {
            try
            {
                (scenario, schedule) = MonteCarlo.ReplayRun(_snapshot, opt.RunIndex, _mcSeed);
            }
            catch
            {
                scenario = _snapshot;
                schedule = _deterministic;
            }
        }

        Data = new GanttData { Scenario = scenario, Schedule = schedule };
        CurrentBlock = 0;
        OnPropertyChanged(nameof(ClockText));
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!IsPlaying) return;
        double next = CurrentBlock + Speed / Fps;
        if (next >= MaxBlock)
        {
            if (Loop) { CurrentBlock = 0; return; }
            CurrentBlock = MaxBlock;
            Pause();
            return;
        }
        CurrentBlock = next;
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (!HasRun) return;
        if (IsPlaying) { Pause(); return; }
        if (CurrentBlock >= MaxBlock) CurrentBlock = 0;
        IsPlaying = true;
        _timer.Start();
    }

    private void Pause()
    {
        IsPlaying = false;
        _timer.Stop();
    }

    [RelayCommand]
    private void StepForward()
    {
        Pause();
        CurrentBlock = Math.Min(MaxBlock, Math.Floor(CurrentBlock) + 1);
    }

    [RelayCommand]
    private void StepBack()
    {
        Pause();
        CurrentBlock = Math.Max(0, Math.Ceiling(CurrentBlock) - 1);
    }

    [RelayCommand]
    private void Restart() => CurrentBlock = 0;
}

public sealed class RunOption
{
    public RunOption(string label, int runIndex, bool isDeterministic)
    {
        Label = label;
        RunIndex = runIndex;
        IsDeterministic = isDeterministic;
    }

    public string Label { get; }
    public int RunIndex { get; }
    public bool IsDeterministic { get; }

    public override string ToString() => Label;
}
