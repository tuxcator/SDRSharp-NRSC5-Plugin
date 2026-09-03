using System.ComponentModel;
using System.Windows.Forms;
using SDRSharp.Common;
using SDRSharp.Radio;

namespace SDRSharp.NRSC5;

/// <summary>
/// The entry point SDR# loads. Its whole job is lifecycle: register the two stream
/// hooks, build the panel when SDR# first asks to show it, and take everything back
/// down on close.
///
/// The plugin is a guest here. SDR# owns the receiver, the threads and the audio
/// device; this class never opens hardware, it only taps the streams SDR# is already
/// producing. That is what lets the plugin decode the digital sidebands while the
/// analog programme keeps playing from the same tuner.
/// </summary>
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

    /// <summary>The panel, built on first access rather than at startup: see <see cref="LoadGui"/>.</summary>
    public UserControl Gui
    {
        get
        {
            LoadGui();
            return _panel!;
        }
    }

    /// <summary>
    /// Called once when SDR# loads the plugin, before any window exists. The hooks are
    /// registered here rather than with the panel so that decoding survives the panel
    /// being closed and reopened.
    /// </summary>
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

    /// <summary>
    /// Builds the panel on demand. SDR# calls this the first time the plugin is shown,
    /// so a user who never opens it pays nothing for the controls.
    /// </summary>
    public void LoadGui()
    {
        if (_panel is null)
        {
            if (_control is null || _engine is null)
                throw new InvalidOperationException("The plugin has not been initialized by SDR# yet.");

            _panel = new Nrsc5Panel(_control, _engine);
        }
    }

    /// <summary>
    /// Unhooks everything on the way out. The hooks must come off before the engine is
    /// disposed, or SDR# could call into a decoder that has already released its native
    /// session.
    /// </summary>
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

    /// <summary>
    /// Follows the dial. The two frequencies mean different things and are deliberately
    /// handled differently: moving the VFO is a new station and restarts the decoder,
    /// while recentring the spectrum only moves the digital mixer.
    /// </summary>
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
