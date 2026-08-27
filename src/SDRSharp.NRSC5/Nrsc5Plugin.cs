using System.ComponentModel;
using System.Windows.Forms;
using SDRSharp.Common;
using SDRSharp.Radio;

namespace SDRSharp.NRSC5;

public sealed class Nrsc5Plugin : ISharpPlugin, ICanLazyLoadGui, ISupportStatus, IExtendedNameProvider
{
    private ISharpControl? _control;
    private Nrsc5Engine? _engine;
    private IqHook? _iqHook;
    private AudioHook? _audioHook;
    private Nrsc5Panel? _panel;
    private long _lastFrequency;

    public string DisplayName => "NRSC-5 HD Radio by tuxcator";
    public string Category => "Digital Radio";
    public string MenuItemName => "NRSC-5 HD Radio by tuxcator";
    public bool IsActive => _panel is { Visible: true };

    public UserControl Gui
    {
        get
        {
            LoadGui();
            return _panel!;
        }
    }

    public void Initialize(ISharpControl control)
    {
        _control = control;
        _lastFrequency = control.Frequency;
        _engine = new Nrsc5Engine();
        _engine.SetTuningOffset(control.Frequency - control.CenterFrequency);
        _iqHook = new IqHook(_engine);
        _audioHook = new AudioHook(_engine);
        control.RegisterStreamHook(_iqHook, ProcessorType.RawIQ);
        control.RegisterStreamHook(_audioHook, ProcessorType.MonitorAF);
        control.PropertyChanged += ControlOnPropertyChanged;
    }

    public void LoadGui()
    {
        if (_panel is null)
        {
            if (_control is null || _engine is null)
                throw new InvalidOperationException("The plugin has not been initialized by SDR# yet.");

            _panel = new Nrsc5Panel(_control, _engine);
        }
    }

    public void Close()
    {
        if (_control is not null)
        {
            _control.PropertyChanged -= ControlOnPropertyChanged;
            if (_iqHook is not null) _control.UnregisterStreamHook(_iqHook);
            if (_audioHook is not null) _control.UnregisterStreamHook(_audioHook);
        }

        _panel?.Dispose();
        _engine?.Dispose();
        _panel = null;
        _engine = null;
        _iqHook = null;
        _audioHook = null;
        _control = null;
        _lastFrequency = 0;
    }

    private void ControlOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_control is null || _engine is null) return;

        if (e.PropertyName == nameof(ISharpControl.Frequency))
        {
            var frequency = _control.Frequency;
            _engine.SetTuningOffset(frequency - _control.CenterFrequency);
            if (frequency == _lastFrequency) return;
            _lastFrequency = frequency;
            _engine.NotifyFrequencyChanged(frequency);
        }
        else if (e.PropertyName == nameof(ISharpControl.CenterFrequency))
        {
            // Recentring the SDR spectrum changes only the digital mixer offset.
            // It must not restart the NRSC-5 decoder or flush buffered HD audio.
            _engine.SetTuningOffset(_control.Frequency - _control.CenterFrequency);
        }
    }
}
