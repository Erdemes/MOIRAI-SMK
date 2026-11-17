using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace MOIRAI.Views.Behaviors;

public static class MiddleButtonPan
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("Enabled", typeof(MiddleButtonPan));

    public static void SetEnabled(ScrollViewer host, bool value) => host.SetValue(EnabledProperty, value);
    public static bool GetEnabled(ScrollViewer host) => host.GetValue(EnabledProperty);

    private static readonly AttachedProperty<State?> StateProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, State?>("State", typeof(MiddleButtonPan));

    private static readonly Cursor PanCursor = new(StandardCursorType.SizeAll);

    static MiddleButtonPan()
    {
        EnabledProperty.Changed.AddClassHandler<ScrollViewer>((sv, e) =>
        {
            if (e.GetNewValue<bool>())
            {
                if (sv.GetValue(StateProperty) is null) sv.SetValue(StateProperty, new State(sv));
            }
            else if (sv.GetValue(StateProperty) is { } st)
            {
                st.Dispose();
                sv.SetValue(StateProperty, null);
            }
        });
    }

    private sealed class State
    {
        private const double DeadZone = 14;
        private const double Gain = 0.12;

        private readonly ScrollViewer _sv;
        private readonly DispatcherTimer _timer;
        private bool _panning;
        private Point _anchor;
        private Point _current;
        private Cursor? _savedCursor;

        public State(ScrollViewer sv)
        {
            _sv = sv;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
            _timer.Tick += Tick;

            _sv.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
            _sv.AddHandler(InputElement.PointerMovedEvent, OnMoved,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            _sv.PointerCaptureLost += OnCaptureLost;
        }

        public void Dispose()
        {
            Stop(null);
            _timer.Tick -= Tick;
            _sv.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
            _sv.RemoveHandler(InputElement.PointerMovedEvent, OnMoved);
            _sv.PointerCaptureLost -= OnCaptureLost;
        }

        private void OnPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_panning)
            {
                Stop(e.Pointer);
                e.Handled = true;
                return;
            }
            if (!e.GetCurrentPoint(_sv).Properties.IsMiddleButtonPressed) return;
            _anchor = _current = e.GetPosition(_sv);
            _panning = true;
            e.Pointer.Capture(_sv);
            _savedCursor = _sv.Cursor;
            _sv.Cursor = PanCursor;
            _timer.Start();
            e.Handled = true;
        }

        private void OnMoved(object? sender, PointerEventArgs e)
        {
            if (_panning) _current = e.GetPosition(_sv);
        }

        private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e) => Stop(null);

        private void Stop(IPointer? pointer)
        {
            if (!_panning) return;
            _panning = false;
            _timer.Stop();
            pointer?.Capture(null);
            _sv.Cursor = _savedCursor;
        }

        private void Tick(object? sender, EventArgs e)
        {
            if (!_panning) return;
            double vx = Velocity(_current.X - _anchor.X);
            double vy = Velocity(_current.Y - _anchor.Y);
            if (vx == 0 && vy == 0) return;

            var o = _sv.Offset;
            double maxX = Math.Max(0, _sv.Extent.Width - _sv.Viewport.Width);
            double maxY = Math.Max(0, _sv.Extent.Height - _sv.Viewport.Height);
            _sv.Offset = new Vector(Math.Clamp(o.X + vx, 0, maxX), Math.Clamp(o.Y + vy, 0, maxY));
        }

        private static double Velocity(double distance)
        {
            double mag = Math.Abs(distance);
            return mag <= DeadZone ? 0 : Math.Sign(distance) * (mag - DeadZone) * Gain;
        }
    }
}
