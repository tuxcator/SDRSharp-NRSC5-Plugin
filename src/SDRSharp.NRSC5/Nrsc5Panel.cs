using System.Drawing;
using System.Windows.Forms;
using SDRSharp.Common;

namespace SDRSharp.NRSC5;

/// <summary>
/// Fonts owned by the panel. They used to be constructed inline at every call site,
/// which leaked a GDI handle per control each time the panel was rebuilt.
/// </summary>
internal static class PanelFonts
{
    internal static readonly Font Base = new("Segoe UI", 9F);
    internal static readonly Font Header = new("Segoe UI Semibold", 18F, FontStyle.Bold);
    internal static readonly Font Byline = new("Segoe UI", 7.5F, FontStyle.Bold);
    internal static readonly Font Station = new("Segoe UI", 16F, FontStyle.Bold);
    internal static readonly Font Title = new("Segoe UI", 13F, FontStyle.Bold);
    internal static readonly Font Detail = new("Segoe UI", 10F);
    internal static readonly Font Small = new("Segoe UI", 9F);
    internal static readonly Font Section = new("Segoe UI", 8F, FontStyle.Bold);
    internal static readonly Font Caption = new("Segoe UI", 7.5F, FontStyle.Bold);
    internal static readonly Font MetricValue = new("Segoe UI Semibold", 12F, FontStyle.Bold);
    internal static readonly Font ChannelButton = new("Segoe UI Semibold", 8.5F, FontStyle.Bold);
    internal static readonly Font SmallButton = new("Segoe UI", 8F);
    internal static readonly Font Placeholder = new("Segoe UI Semibold", 16F, FontStyle.Bold);
    internal static readonly Font Slogan = new("Segoe UI", 9.5F, FontStyle.Italic);
    internal static readonly Font InfoValue = new("Segoe UI Semibold", 9F, FontStyle.Bold);
    internal static readonly Font InfoCaption = new("Segoe UI", 7F, FontStyle.Bold);
}

internal sealed class Nrsc5Panel : UserControl
{
    internal const string DevelopmentVersion = PluginInfo.DevelopmentVersion;

    private static readonly Color Background = Color.FromArgb(10, 15, 21);
    private static readonly Color Card = Color.FromArgb(21, 29, 38);
    private static readonly Color Primary = Color.FromArgb(47, 211, 198);
    private static readonly Color Secondary = Color.FromArgb(83, 159, 255);
    private static readonly Color Muted = Color.FromArgb(145, 160, 178);

    private static readonly Color SurroundOn = Color.FromArgb(17, 71, 69);

    private readonly Nrsc5Engine _engine;
    private readonly Button _surround = NewFlatButton("Surround");
    private readonly Button _trafficButton = NewFlatButton("Traffic map");
    private readonly Button _weatherButton = NewFlatButton("Weather map");
    private HereMapsForm? _maps;
    private readonly CheckBox _enabled = NewCheckBox("Enable HD decoding");
    private readonly CheckBox _replaceAudio = NewCheckBox("Auto HD audio", true);
    private readonly CheckBox _useBuffer = NewCheckBox("Buffer", true);
    private readonly NumericUpDown _bufferSeconds = new()
    {
        Minimum = (decimal)Nrsc5Engine.MinBufferSeconds,
        Maximum = (decimal)Nrsc5Engine.MaxBufferSeconds,
        Value = (decimal)Nrsc5Engine.DefaultBufferSeconds,
        Increment = 0.05M,
        DecimalPlaces = 2,
        Width = 62
    };
    private readonly Label _bufferState = new()
    {
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Muted,
        Font = PanelFonts.Caption
    };
    private readonly Label _programDisplay = NewCenterLabel(PanelFonts.Station, Primary);
    private readonly NumericUpDown _calibration = new() { Minimum = -100, Maximum = 20, Value = -30, Width = 64, DecimalPlaces = 0 };
    private readonly Label _state = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Muted };
    private readonly Label _station = NewCenterLabel(PanelFonts.Station, Primary);
    private readonly Label _title = NewCenterLabel(PanelFonts.Title, Color.White);
    private readonly Label _artistAlbum = NewCenterLabel(PanelFonts.Detail, Muted);
    private readonly Label _iqInfo = NewCenterLabel(PanelFonts.Small, Muted);
    private readonly Label _slogan = NewCenterLabel(PanelFonts.Slogan, Secondary);
    private readonly InfoCard _piCode = new("PI CODE", Secondary);
    private readonly InfoCard _location = new("LOCATION", Primary);
    private readonly InfoCard _erp = new("POWER", Color.FromArgb(255, 184, 77));
    private readonly PictureBox _artwork = new()
    {
        SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = Color.FromArgb(16, 23, 31),
        Dock = DockStyle.None,
        Margin = Padding.Empty
    };
    private readonly MetricCard _power = new("RF POWER", "-- dBFS", Primary);
    private readonly MetricCard _dbm = new("ESTIMATED LEVEL", "-- dBm", Color.FromArgb(255, 184, 77));
    private readonly MetricCard _snr = new("SNR / MER", "-- dB", Secondary);
    private readonly MetricCard _bitrate = new("BITRATE HDC", "-- kb/s", Color.FromArgb(196, 128, 255));
    private readonly MetricCard _mer = new("MER L / U", "-- / -- dB", Color.FromArgb(80, 200, 120));
    private readonly MetricCard _ber = new("BER", "--", Color.FromArgb(255, 110, 110));
    private readonly ToolTip _tips = new();
    private byte[]? _artworkReference;
    private bool _artworkIsLogo;

    public Nrsc5Panel(ISharpControl control, Nrsc5Engine engine)
    {
        _ = control;
        _engine = engine;
        AutoScroll = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Background;
        ForeColor = Color.White;
        Padding = new Padding(6);
        Font = PanelFonts.Base;

        _programDisplay.Text = "HD1";
        _calibration.BackColor = Card;
        _calibration.ForeColor = Color.White;
        _bufferSeconds.BackColor = Card;
        _bufferSeconds.ForeColor = Color.White;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 1,
            RowCount = 14,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
            BackColor = Background,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        // Every absolute row here is subtracted from the two percent rows that carry the
        // Artwork and the metric cards. Docked into the narrow left column of SDR# the
        // panel only gets ~440 px, so the fixed rows are kept as lean as they can be while
        // staying legible; anything added here comes straight out of the Artwork.
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 17));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(_state, 0, 1);
        root.Controls.Add(BuildArtworkHost(), 0, 2);
        root.Controls.Add(_station, 0, 3);
        root.Controls.Add(_title, 0, 4);
        root.Controls.Add(_artistAlbum, 0, 5);
        root.Controls.Add(_slogan, 0, 6);
        root.Controls.Add(BuildStationInfo(), 0, 7);
        root.Controls.Add(BuildChannelSelector(), 0, 8);
        root.Controls.Add(BuildSectionLabel("SIGNAL ANALYSIS"), 0, 9);
        root.Controls.Add(BuildMetrics(), 0, 10);
        root.Controls.Add(_iqInfo, 0, 11);
        root.Controls.Add(_bufferState, 0, 12);
        root.Controls.Add(BuildControls(), 0, 13);
        Controls.Add(root);

        _artwork.Paint += ArtworkOnPaint;
        _enabled.CheckedChanged += (_, _) => _engine.Enabled = _enabled.Checked;
        _replaceAudio.CheckedChanged += (_, _) => _engine.ReplaceAnalogAudio = _replaceAudio.Checked;
        _calibration.ValueChanged += (_, _) => _engine.DbmCalibrationOffset = (float)_calibration.Value;
        _useBuffer.CheckedChanged += (_, _) =>
        {
            _engine.BufferingEnabled = _useBuffer.Checked;
            _bufferSeconds.Enabled = _useBuffer.Checked;
        };
        _bufferSeconds.ValueChanged += (_, _) => _engine.BufferSeconds = (double)_bufferSeconds.Value;
        _surround.Click += (_, _) =>
        {
            _engine.SurroundEnabled = !_engine.SurroundEnabled;
            ApplySurroundLook();
        };
        ApplySurroundLook();

        _engine.BufferingEnabled = _useBuffer.Checked;
        _engine.BufferSeconds = (double)_bufferSeconds.Value;
        _engine.StatusChanged += EngineOnStatusChanged;
        _engine.StationFactsChanged += EngineOnStationFactsChanged;
        _engine.HereDataChanged += EngineOnHereDataChanged;
        EngineOnStatusChanged(_engine.Status);
        EngineOnStationFactsChanged(_engine.Facts);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _engine.StatusChanged -= EngineOnStatusChanged;
            _engine.StationFactsChanged -= EngineOnStationFactsChanged;
            _engine.HereDataChanged -= EngineOnHereDataChanged;
            _maps?.Dispose();
            _maps = null;
            _tips.Dispose();
            var image = _artwork.Image;
            _artwork.Image = null;
            image?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// The title block. The byline carries the development build number so a tester can tell
    /// at a glance which version is loaded, which matters when the plugin is installed by
    /// copying a DLL over another one.
    /// </summary>
    private Control BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Card, Margin = new Padding(0, 0, 0, 3) };
        var title = new Label
        {
            Text = "NRSC-5  HD RADIO",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = PanelFonts.Header,
            ForeColor = Primary
        };
        var author = new Label
        {
            Text = $"PROFESSIONAL MONITOR · BY TUXCATOR · DEV {DevelopmentVersion}",
            Dock = DockStyle.Bottom,
            Height = 19,
            TextAlign = ContentAlignment.TopCenter,
            Font = PanelFonts.Byline,
            ForeColor = Muted
        };
        panel.Controls.Add(title);
        panel.Controls.Add(author);
        return panel;
    }

    /// <summary>
    /// A host whose only job is to keep the artwork square. The panel is a tall narrow column,
    /// so the image has to be centred and sized by hand rather than simply docked.
    /// </summary>
    private Control BuildArtworkHost()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Background, Margin = Padding.Empty };
        host.Controls.Add(_artwork);
        host.Resize += (_, _) => LayoutArtworkSquare(host);
        host.Layout += (_, _) => LayoutArtworkSquare(host);
        return host;
    }

    /// <summary>
    /// Sizes the artwork to the largest square that fits, centred. Album art is square and
    /// letterboxing it inside a tall box wastes the panel's scarcest resource.
    /// </summary>
    private void LayoutArtworkSquare(Control host)
    {
        var side = Math.Max(0, Math.Min(host.ClientSize.Width - 8, host.ClientSize.Height - 8));
        _artwork.Bounds = new Rectangle(
            (host.ClientSize.Width - side) / 2,
            (host.ClientSize.Height - side) / 2,
            side,
            side);
    }

    /// <summary>
    /// Who the station is, as opposed to how well it is being received. The three cells
    /// are kept on one row because every absolute pixel here comes out of the Artwork.
    /// The detail that does not fit - licensee, class, HAAT, transmitter site - lives in
    /// the tooltip rather than costing another row.
    /// </summary>
    private Control BuildStationInfo()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Background,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        // La ciudad es el campo que mas espacio necesita: el PI code y la potencia son
        // siempre cortos, asi que se les da lo justo y el resto va a la ubicacion.
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        grid.Controls.Add(_piCode, 0, 0);
        grid.Controls.Add(_location, 1, 0);
        grid.Controls.Add(_erp, 2, 0);

        _tips.SetToolTip(_slogan, "Slogan as the station broadcasts it in its SIS frames.");
        _tips.SetToolTip(_piCode, "RDS PI code in hexadecimal, derived from the call sign. HD Radio does not transmit it.");
        return grid;
    }

    /// <summary>
    /// Opens the map window, or brings it back if it was closed. The window is created
    /// on demand and seeded with whatever has already arrived, so opening it after a
    /// station has been decoding for a while shows the map rather than an empty frame.
    /// </summary>
    private void OpenMaps(HereView view)
    {
        if (_maps is null || _maps.IsDisposed)
        {
            _maps = new HereMapsForm();
            _maps.FormClosed += (_, _) => _maps = null;
            _maps.SetData(_engine.HereData);
        }
        // Owned by SDR#'s window, so it floats above it. Without an owner it dropped
        // behind the main window the moment SDR# took focus back, which made the two
        // buttons look as though they did nothing.
        _maps.Owner = FindForm();
        _maps.ShowSection(view);
    }

    private void EngineOnHereDataChanged(HereData data) => _maps?.SetData(data);

    private void EngineOnStationFactsChanged(StationFacts facts)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => EngineOnStationFactsChanged(facts)));
            return;
        }

        _slogan.Text = facts.Slogan.Length > 0 ? facts.Slogan : facts.Message;
        _piCode.SetValue(facts.PiCode is { } pi ? pi.ToString("X4") : "--");
        _location.SetValue(DescribeLocation(facts));
        _erp.SetValue(facts.ErpKw > 0 ? $"{facts.ErpKw:0.##} kW" : "--");

        var details = DescribeStation(facts);
        _tips.SetToolTip(_location, details);
        _tips.SetToolTip(_erp, details);
    }

    /// <summary>
    /// The town the transmitter stands in, which is what a listener wants when they are
    /// deciding where to point an antenna. It is regularly not the community of licence -
    /// KQRS is licensed to Golden Valley and transmits from Shoreview - so the licence
    /// town is the fallback rather than the headline, and both are in the tooltip.
    ///
    /// Raw coordinates are the last resort: they are the one thing here that reads as a
    /// failure rather than as an answer.
    /// </summary>
    private static string DescribeLocation(StationFacts facts)
    {
        // A site in the wrong country is the station's own misconfiguration. The raw
        // coordinates are still what it broadcasts, so they are shown, flagged, rather
        // than dressed up as a town it demonstrably does not transmit from.
        if (facts.SiteContradictsCallsign) return $"{facts.Latitude:0.00}, {facts.Longitude:0.00}  ?";
        if (facts.SitePlace.Length > 0) return facts.SitePlace;
        if (facts.Place.Length > 0) return facts.Place;
        if (facts.SiteLookup == StationLookupState.Pending || facts.Lookup == StationLookupState.Pending)
            return "Looking up...";
        if (facts.HasLocation)
            return $"{Math.Abs(facts.Latitude):0.000}{(facts.Latitude < 0 ? "S" : "N")}  {Math.Abs(facts.Longitude):0.000}{(facts.Longitude < 0 ? "W" : "E")}";
        return "--";
    }

    private static string DescribeStation(StationFacts facts)
    {
        var lines = new List<string>(7);
        if (facts.Callsign.Length > 0) lines.Add($"Call sign: {facts.Callsign}");
        if (facts.FacilityId > 0) lines.Add($"FCC facility ID: {facts.FacilityId}");
        if (facts.Licensee.Length > 0) lines.Add($"Licensee: {facts.Licensee}");
        if (facts.StationClass.Length > 0) lines.Add($"Class: {facts.StationClass}");
        if (facts.HaatMeters > 0) lines.Add($"HAAT: {facts.HaatMeters:0.#} m");
        // Spelt out side by side, because the two towns differ often enough that showing
        // one of them unlabelled would be quietly misleading.
        if (facts.SiteContradictsCallsign)
            lines.Add($"The broadcast site falls in {facts.SiteCountry} but the call sign is " +
                      $"{facts.CallsignCountry}. This station's SIS location is wrong; " +
                      $"it points at {facts.SitePlace}.");
        else if (facts.SitePlace.Length > 0)
            lines.Add($"Transmitter town: {facts.SitePlace}" +
                      (facts.SiteSource.Length > 0 ? $" ({facts.SiteSource})" : ""));
        if (facts.Place.Length > 0) lines.Add($"Community of licence: {facts.Place}");
        if (facts.HasLocation)
            lines.Add($"Transmitter site: {facts.Latitude:0.0000}, {facts.Longitude:0.0000} at {facts.Altitude} m");
        lines.Add(facts.Lookup switch
        {
            StationLookupState.Pending => "Querying the FCC licence database...",
            StationLookupState.Resolved => "Licence data: FCC FM Query. Slogan and coordinates: SIS.",
            StationLookupState.NotFound => "This facility ID is not in the FCC FM database.",
            StationLookupState.Failed => "The FCC lookup failed; retrying on the next tune.",
            StationLookupState.Unsupported => $"Licensed outside the US ({facts.CountryCode}); no FCC record.",
            _ => "Waiting for the station to identify itself."
        });
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The six signal cards. Laid out as a percentage grid so they grow with the panel rather
    /// than clipping, which is what lets the same panel work docked and floating.
    /// </summary>
    private Control BuildMetrics()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            BackColor = Background,
            Padding = new Padding(0, 1, 0, 1),
            Margin = Padding.Empty
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var i = 0; i < 3; i++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 33.333F));
        grid.Controls.Add(_power, 0, 0);
        grid.Controls.Add(_dbm, 1, 0);
        grid.Controls.Add(_snr, 0, 1);
        grid.Controls.Add(_bitrate, 1, 1);
        grid.Controls.Add(_mer, 0, 2);
        grid.Controls.Add(_ber, 1, 2);
        return grid;
    }

    /// <summary>
    /// Previous and Next around the current subchannel. A pair of buttons rather than a drop
    /// down: the list of subchannels changes as the station is discovered, and a menu that
    /// rewrites itself under the pointer is worse than two arrows.
    /// </summary>
    private Control BuildChannelSelector()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Card,
            Padding = new Padding(5),
            Margin = new Padding(0, 3, 0, 3)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));

        var previous = NewChannelButton("◀  PREVIOUS");
        var next = NewChannelButton("NEXT  ▶");
        _programDisplay.Dock = DockStyle.Fill;
        _programDisplay.Height = 52;
        _programDisplay.TextAlign = ContentAlignment.MiddleCenter;
        _programDisplay.BackColor = Color.FromArgb(14, 21, 29);
        _programDisplay.Margin = new Padding(5, 0, 5, 0);

        previous.Click += (_, _) => _engine.StepProgram(-1);
        next.Click += (_, _) => _engine.StepProgram(1);
        panel.Controls.Add(previous, 0, 0);
        panel.Controls.Add(_programDisplay, 1, 0);
        panel.Controls.Add(next, 2, 0);
        _tips.SetToolTip(_programDisplay, "Only the subchannels the station actually broadcasts are selectable.");
        return panel;
    }

    private static Button NewChannelButton(string text)
    {
        var button = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(28, 43, 55),
            ForeColor = Color.White,
            Font = PanelFonts.ChannelButton,
            Cursor = Cursors.Hand,
            Margin = new Padding(2)
        };
        button.FlatAppearance.BorderColor = Secondary;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, 70, 86);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(25, 115, 120);
        return button;
    }

    /// <summary>
    /// The controls at the foot of the panel. Every row here is absolute height taken out of
    /// the artwork, so the labels are terse on purpose and nothing is added lightly.
    /// </summary>
    private Control BuildControls()
    {
        var container = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Card,
            Padding = new Padding(3, 2, 3, 2),
            Margin = new Padding(0, 3, 0, 0)
        };
        for (var i = 0; i < 4; i++) container.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));

        // Labels stay terse on purpose: docked, the panel is only ~250 px wide and
        // anything longer is clipped rather than wrapped.
        var toggles = NewRow();
        toggles.Controls.Add(_enabled);
        toggles.Controls.Add(_replaceAudio);

        var buffering = NewRow();
        buffering.Controls.Add(_useBuffer);
        buffering.Controls.Add(_bufferSeconds);
        buffering.Controls.Add(NewCaption("s"));
        buffering.Controls.Add(NewCaption("dBm"));
        buffering.Controls.Add(_calibration);

        var restart = NewFlatButton("Restart decoder");
        restart.Click += (_, _) => _engine.Restart();

        // Both buttons share the last row: the panel cannot afford a fourth one.
        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Card,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        actions.Controls.Add(restart, 0, 0);
        actions.Controls.Add(_surround, 1, 0);

        // The data services get their own row: a map needs a window, not a corner of a
        // panel docked into 250 px, so these two only open one.
        var maps = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Card,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        maps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        maps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        maps.Controls.Add(_trafficButton, 0, 0);
        maps.Controls.Add(_weatherButton, 1, 0);

        _trafficButton.Click += (_, _) => OpenMaps(HereView.Traffic);
        _weatherButton.Click += (_, _) => OpenMaps(HereView.Weather);

        container.Controls.Add(toggles, 0, 0);
        container.Controls.Add(buffering, 0, 1);
        container.Controls.Add(actions, 0, 2);
        container.Controls.Add(maps, 0, 3);

        _tips.SetToolTip(_calibration, "dBm is an estimate. Adjust this offset using a known reference signal.");
        _tips.SetToolTip(_useBuffer, "Turn off for the lowest latency. Leave on to ride out brief signal dropouts.");
        _tips.SetToolTip(_bufferSeconds, "How much HD audio to accumulate before it replaces the analog path.");
        _tips.SetToolTip(_surround, "Widens the HD stereo image. Only affects HD audio, never the analog path.");
        _tips.SetToolTip(_trafficButton, "Traffic map, weather map and emergency alerts, in their own window.");
        _tips.SetToolTip(_weatherButton, "Traffic map, weather map and emergency alerts, in their own window.");
        return container;
    }

    private static FlowLayoutPanel NewRow() => new()
    {
        Dock = DockStyle.Fill,
        WrapContents = false,
        FlowDirection = FlowDirection.LeftToRight,
        BackColor = Card,
        Margin = Padding.Empty
    };

    /// <summary>
    /// Paints the signal side of the panel. Fires about ten times a second from a decoder
    /// thread, so it marshals to the UI thread and does nothing but assign strings.
    /// </summary>
    private void EngineOnStatusChanged(Nrsc5Status status)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => EngineOnStatusChanged(status)));
            return;
        }

        _state.Text = status.Message;
        _state.ForeColor = status.Synced ? Primary : Muted;
        _programDisplay.Text = $"HD{status.SelectedProgram + 1}";
        _programDisplay.ForeColor = status.ProgramMask == 0 || status.HasProgram(status.SelectedProgram)
            ? Primary
            : Muted;
        _station.Text = string.IsNullOrWhiteSpace(status.Station) ? "STATION NOT IDENTIFIED" : status.Station.ToUpperInvariant();
        _title.Text = string.IsNullOrWhiteSpace(status.Title) ? "Waiting for song information..." : status.Title;
        var artist = string.IsNullOrWhiteSpace(status.Artist) ? "--" : status.Artist;
        _artistAlbum.Text = string.IsNullOrWhiteSpace(status.Album) ? artist : $"{artist}  ·  {status.Album}";
        _power.SetValue($"{status.SignalDbfs:0.0} dBFS", StrengthColor(status.SignalDbfs));
        _dbm.SetValue($"{status.EstimatedDbm:0.0} dBm");
        _snr.SetValue(status.Synced ? $"{status.SnrDb:0.0} dB" : "-- dB");
        _bitrate.SetValue(status.BitrateKbps > 0 ? $"{status.BitrateKbps:0.0} kb/s" : "-- kb/s");
        _mer.SetValue(status.Synced ? $"{status.MerLower:0.0} / {status.MerUpper:0.0} dB" : "-- / -- dB");
        _ber.SetValue(status.Synced ? status.Ber.ToString("0.0000") : "--");
        _iqInfo.Text = $"IQ {status.InputRate / 1000:0.0} kS/s   ·   VFO {status.OffsetHz / 1000:+0.0;-0.0;0.0} kHz   ·   PEAK {status.PeakDbfs:0.0} dBFS";
        _bufferState.Text = status.BufferTargetSeconds <= 0
            ? $"BUFFER OFF   ·   HELD {status.BufferedSeconds:0.00} s   ·   PROGRAMS {DescribePrograms(status)}"
            : $"BUFFER {status.BufferedSeconds:0.00} / {status.BufferTargetSeconds:0.00} s   ·   PROGRAMS {DescribePrograms(status)}";
        _bufferState.ForeColor = status.BufferTargetSeconds > 0 && status.BufferedSeconds >= status.BufferTargetSeconds
            ? Primary
            : Muted;
        SetArtwork(status.Artwork, status.ArtworkIsStationLogo);
    }

    /// <summary>
    /// Lists the subchannels the station actually broadcasts, as discovered from the SIG table.
    /// </summary>
    private static string DescribePrograms(Nrsc5Status status)
    {
        if (status.ProgramMask == 0) return "--";
        var names = new List<string>(8);
        for (var index = 0; index < 8; index++)
            if (status.HasProgram(index)) names.Add($"HD{index + 1}");
        return string.Join(" ", names);
    }

    /// <summary>
    /// Swaps the artwork, comparing by reference first so an unchanged image is not decoded
    /// and reallocated ten times a second. The old bitmap is disposed explicitly: GDI handles
    /// are not memory and the collector is in no hurry to release them.
    /// </summary>
    private void SetArtwork(byte[]? bytes, bool isStationLogo)
    {
        if (ReferenceEquals(bytes, _artworkReference) && isStationLogo == _artworkIsLogo) return;
        _artworkReference = bytes;
        _artworkIsLogo = isStationLogo;
        var previous = _artwork.Image;
        _artwork.Image = null;
        previous?.Dispose();
        if (bytes is { Length: > 0 })
        {
            try
            {
                _artwork.Image = DecodeArtwork(bytes);
            }
            catch
            {
                _artwork.Image = null;
            }
        }
        _tips.SetToolTip(_artwork, isStationLogo ? "Station logo (no album art received yet)" : string.Empty);
        _artwork.Invalidate();
    }

    /// <summary>
    /// Decodes a broadcast image, honouring the EXIF orientation tag. Stations do send
    /// sideways artwork, and a receiver that ignores the tag displays it sideways.
    /// </summary>
    private static Bitmap? DecodeArtwork(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var decoded = Image.FromStream(stream, true, true);
        if (decoded.Width > 4096 || decoded.Height > 4096) return null;

        const int orientationProperty = 0x0112;
        if (decoded.PropertyIdList.Contains(orientationProperty))
        {
            var value = decoded.GetPropertyItem(orientationProperty)?.Value;
            if (value is { Length: >= 2 })
            {
                var orientation = BitConverter.ToUInt16(value, 0);
                var rotation = orientation switch
                {
                    2 => RotateFlipType.RotateNoneFlipX,
                    3 => RotateFlipType.Rotate180FlipNone,
                    4 => RotateFlipType.Rotate180FlipX,
                    5 => RotateFlipType.Rotate90FlipX,
                    6 => RotateFlipType.Rotate90FlipNone,
                    7 => RotateFlipType.Rotate270FlipX,
                    8 => RotateFlipType.Rotate270FlipNone,
                    _ => RotateFlipType.RotateNoneFlipNone
                };
                if (rotation != RotateFlipType.RotateNoneFlipNone) decoded.RotateFlip(rotation);
            }
        }

        return new Bitmap(decoded);
    }

    private void ArtworkOnPaint(object? sender, PaintEventArgs e)
    {
        if (_artwork.Image is not null || _artwork.Width < 20 || _artwork.Height < 20) return;
        using var pen = new Pen(Color.FromArgb(55, Primary), 2);
        e.Graphics.DrawRectangle(pen, 8, 8, _artwork.Width - 17, _artwork.Height - 17);
        TextRenderer.DrawText(e.Graphics, "HD\nARTWORK", PanelFonts.Placeholder,
            _artwork.ClientRectangle, Color.FromArgb(85, 105, 120),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }

    private static Color StrengthColor(float dbfs) => dbfs switch
    {
        > -25 => Color.FromArgb(255, 193, 71),
        > -45 => Primary,
        > -70 => Secondary,
        _ => Color.FromArgb(255, 110, 110)
    };

    private static Label BuildSectionLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.BottomLeft,
        Font = PanelFonts.Section,
        ForeColor = Muted,
        Padding = new Padding(4, 0, 0, 2)
    };

    private static Label NewCenterLabel(Font font, Color color) => new()
    {
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = font,
        ForeColor = color,
        AutoEllipsis = true
    };

    private static Button NewFlatButton(string text)
    {
        var button = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(33, 46, 59),
            ForeColor = Color.White,
            Font = PanelFonts.SmallButton,
            Margin = new Padding(2, 2, 2, 1)
        };
        button.FlatAppearance.BorderColor = Secondary;
        return button;
    }

    /// <summary>
    /// The button is its own indicator. The state is spelled out in the caption because
    /// SDR# themes plugin controls after they are built and overrides the colours.
    /// </summary>
    private void ApplySurroundLook()
    {
        var active = _engine.SurroundEnabled;
        _surround.Text = active ? "Surround ON" : "Surround OFF";
        _surround.BackColor = active ? SurroundOn : Color.FromArgb(33, 46, 59);
        _surround.ForeColor = active ? Primary : Color.White;
        _surround.FlatAppearance.BorderColor = active ? Primary : Secondary;
    }

    private static CheckBox NewCheckBox(string text, bool isChecked = false) => new()
    {
        Text = text,
        AutoSize = true,
        Checked = isChecked,
        ForeColor = Color.White,
        BackColor = Card,
        Margin = new Padding(3, 4, 3, 4)
    };

    private static Label NewCaption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Muted,
        BackColor = Card,
        Margin = new Padding(3, 6, 1, 0)
    };
}

/// <summary>
/// A shorter <see cref="MetricCard"/>: same caption-over-value shape, sized to fit three
/// across a single 37 px row instead of half of a metrics cell.
/// </summary>
internal sealed class InfoCard : Panel
{
    private readonly SingleLineLabel _value;

    public InfoCard(string caption, Color accent)
    {
        Dock = DockStyle.Fill;
        Margin = new Padding(3, 0, 3, 0);
        Padding = new Padding(4, 1, 2, 1);
        BackColor = Color.FromArgb(21, 29, 38);
        _value = new SingleLineLabel
        {
            Text = "--",
            Dock = DockStyle.Fill,
            ForeColor = accent,
            Font = PanelFonts.InfoValue
        };
        Controls.Add(_value);
        Controls.Add(new Label
        {
            Text = caption,
            Dock = DockStyle.Top,
            Height = 14,
            ForeColor = Color.FromArgb(145, 160, 178),
            Font = PanelFonts.InfoCaption
        });
    }

    public void SetValue(string value) => _value.Text = value.Length == 0 ? "--" : value;

    /// <summary>
    /// A plain Label wraps, and in a 45 px cell that turns "100 kW" into "100" over a "kW"
    /// clipped by the row height. These values are always one line, so they are drawn as
    /// one line and cut with an ellipsis when a long city name does not fit.
    /// </summary>
    private sealed class SingleLineLabel : Label
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }
    }
}

internal sealed class MetricCard : Panel
{
    private readonly Label _value;

    public MetricCard(string caption, string value, Color accent)
    {
        Dock = DockStyle.Fill;
        Margin = new Padding(3);
        Padding = new Padding(8, 5, 8, 5);
        BackColor = Color.FromArgb(21, 29, 38);
        var captionLabel = new Label
        {
            Text = caption,
            Dock = DockStyle.Top,
            Height = 17,
            ForeColor = Color.FromArgb(145, 160, 178),
            Font = PanelFonts.Caption
        };
        _value = new Label
        {
            Text = value,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = accent,
            Font = PanelFonts.MetricValue,
            AutoEllipsis = true
        };
        Controls.Add(_value);
        Controls.Add(captionLabel);
    }

    public void SetValue(string value, Color? color = null)
    {
        _value.Text = value;
        if (color.HasValue) _value.ForeColor = color.Value;
    }
}
