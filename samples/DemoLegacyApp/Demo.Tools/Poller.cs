using System;
using System.Windows.Threading;

namespace Demo.Tools;

/// <summary>Servicio de sondeo periódico. DEPENDENCIA WINDOWS: DispatcherTimer (WPF,
/// System.Windows.Threading) es solo-Windows; el equivalente multiplataforma es un temporizador del BCL
/// (System.Timers.Timer / System.Threading.PeriodicTimer). No es UI: solo dispara un evento a intervalos.</summary>
public sealed class Poller
{
    private readonly DispatcherTimer _timer = new();

    public event EventHandler? Ticked;

    public Poller()
    {
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (s, e) => Ticked?.Invoke(this, EventArgs.Empty);
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
}
