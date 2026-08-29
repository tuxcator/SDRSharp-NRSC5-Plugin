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
}

internal sealed class Nrsc5Panel : UserControl
{
    /// <summary>Build shown in the header byline, so a tester can tell versions apart.</summary>
    internal const string DevelopmentVersion = "3.2";

    private static readonly Color Background = Color.FromArgb(10, 15, 21);
    private static readonly Color Card = Color.FromArgb(21, 29, 38);
    private static readonly Color Primary = Color.FromArgb(47, 211, 198);
    private static readonly Color Secondary = Color.FromArgb(83, 159, 255);
    private static readonly Color Muted = Color.FromArgb(145, 160, 178);

    private static readonly Color SurroundOn = Color.FromArgb(17, 71, 69);

    private readonly Nrsc5Engine _engine;
    private readonly Button _surround = NewFlatButton("Surround");
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
            RowCount = 12,
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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(_state, 0, 1);
        root.Controls.Add(BuildArtworkHost(), 0, 2);
        root.Controls.Add(_station, 0, 3);
        root.Controls.Add(_title, 0, 4);
        root.Controls.Add(_artistAlbum, 0, 5);
        root.Controls.Add(BuildChannelSelector(), 0, 6);
        root.Controls.Add(BuildSectionLabel("SIGNAL ANALYSIS"), 0, 7);
        root.Controls.Add(BuildMetrics(), 0, 8);
        root.Controls.Add(_iqInfo, 0, 9);
        root.Controls.Add(_bufferState, 0, 10);
        root.Controls.Add(BuildControls(), 0, 11);
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
        EngineOnStatusChanged(_engine.Status);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _engine.StatusChanged -= EngineOnStatusChanged;
            _tips.Dispose();
            var image = _artwork.Image;
            _artwork.Image = null;
            image?.Dispose();
        }
        base.Dispose(disposing);
    }

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

    private Control BuildArtworkHost()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Background, Margin = Padding.Empty };
        host.Controls.Add(_artwork);
        host.Resize += (_, _) => LayoutArtworkSquare(host);
        host.Layout += (_, _) => LayoutArtworkSquare(host);
        return host;
    }

    private void LayoutArtworkSquare(Control host)
    {
        var side = Math.Max(0, Math.Min(host.ClientSize.Width - 8, host.ClientSize.Height - 8));
        _artwork.Bounds = new Rectangle(
            (host.ClientSize.Width - side) / 2,
            (host.ClientSize.Height - side) / 2,
            side,
            side);
    }

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

    private Control BuildControls()
    {
        var container = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Card,
            Padding = new Padding(3, 2, 3, 2),
            Margin = new Padding(0, 3, 0, 0)
        };
        for (var i = 0; i < 3; i++) container.RowStyles.Add(new RowStyle(SizeType.Percent, 33.333F));

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

        container.Controls.Add(toggles, 0, 0);
        container.Controls.Add(buffering, 0, 1);
        container.Controls.Add(actions, 0, 2);

        _tips.SetToolTip(_calibration, "dBm is an estimate. Adjust this offset using a known reference signal.");
        _tips.SetToolTip(_useBuffer, "Turn off for the lowest latency. Leave on to ride out brief signal dropouts.");
        _tips.SetToolTip(_bufferSeconds, "How much HD audio to accumulate before it replaces the analog path.");
        _tips.SetToolTip(_surround, "Widens the HD stereo image. Only affects HD audio, never the analog path.");
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

    private static string DescribePrograms(Nrsc5Status status)
    {
        if (status.ProgramMask == 0) return "--";
        var names = new List<string>(8);
        for (var index = 0; index < 8; index++)
            if (status.HasProgram(index)) names.Add($"HD{index + 1}");
        return string.Join(" ", names);
    }

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
