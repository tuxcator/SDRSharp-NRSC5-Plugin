using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SDRSharp.NRSC5;

/// <summary>Which of the three data services the window is showing.</summary>
internal enum HereView
{
    Traffic,
    Weather,
    Alerts
}

/// <summary>
/// The traffic and weather maps, and the emergency alerts, in a window of their own.
/// They do not belong in the panel: a map needs hundreds of pixels in both directions
/// and the panel is docked into a column of about 250, so it is given its own frame that
/// the listener can size and put wherever the screen has room.
///
/// The window is modeless and owns nothing the decoder needs, so closing it costs
/// nothing and tuning carries on behind it.
/// </summary>
internal sealed class HereMapsForm : Form
{
    private static readonly Color Background = Color.FromArgb(10, 15, 21);
    private static readonly Color Card = Color.FromArgb(21, 29, 38);
    private static readonly Color Primary = Color.FromArgb(47, 211, 198);
    private static readonly Color Secondary = Color.FromArgb(83, 159, 255);
    private static readonly Color Muted = Color.FromArgb(145, 160, 178);
    private static readonly Color Alarm = Color.FromArgb(255, 110, 110);

    private readonly Button _traffic = NewTab("TRAFFIC");
    private readonly Button _weather = NewTab("WEATHER");
    private readonly Button _alerts = NewTab("ALERTS");
    private readonly PictureBox _map = new()
    {
        Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = Color.FromArgb(16, 23, 31)
    };
    private readonly Panel _alertList = new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        BackColor = Background,
        Padding = new Padding(6),
        Visible = false
    };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom,
        Height = 44,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Muted,
        BackColor = Card,
        Padding = new Padding(10, 0, 10, 0),
        Font = PanelFonts.Small
    };

    private HereData _data = HereData.Empty;
    private HereView _view = HereView.Traffic;

    public HereMapsForm()
    {
        Text = "NRSC-5 · Traffic, weather and alerts";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(860, 720);
        MinimumSize = new Size(520, 420);
        BackColor = Background;
        ForeColor = Color.White;
        Font = PanelFonts.Base;

        var tabs = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Card,
            Padding = new Padding(4),
            Margin = Padding.Empty
        };
        for (var i = 0; i < 3; i++) tabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
        tabs.Controls.Add(_traffic, 0, 0);
        tabs.Controls.Add(_weather, 1, 0);
        tabs.Controls.Add(_alerts, 2, 0);

        _traffic.Click += (_, _) => ShowSection(HereView.Traffic);
        _weather.Click += (_, _) => ShowSection(HereView.Weather);
        _alerts.Click += (_, _) => ShowSection(HereView.Alerts);

        var content = new Panel { Dock = DockStyle.Fill, BackColor = Background, Padding = new Padding(6) };
        content.Controls.Add(_map);
        content.Controls.Add(_alertList);

        Controls.Add(content);
        Controls.Add(_status);
        Controls.Add(tabs);

        _map.Paint += MapOnPaint;
        Render();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) SetMapImage(null);
        base.Dispose(disposing);
    }

    /// <summary>Brings the window forward on the asked-for section, opening it if needed.</summary>
    public void ShowSection(HereView view)
    {
        _view = view;
        Render();
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    /// <summary>
    /// Feeds the window a new snapshot. Arrives from a decoder thread, so it marshals first.
    /// </summary>
    public void SetData(HereData data)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetData(data)));
            return;
        }
        _data = data;
        Render();
    }

    /// <summary>
    /// Repaints whichever section is showing. Cheap enough to run on every update because a
    /// traffic mosaic changes a few times a minute, not a few times a second.
    /// </summary>
    private void Render()
    {
        foreach (var (button, view) in new[] { (_traffic, HereView.Traffic), (_weather, HereView.Weather), (_alerts, HereView.Alerts) })
        {
            var active = view == _view;
            button.BackColor = active ? Color.FromArgb(17, 71, 69) : Color.FromArgb(28, 43, 55);
            button.ForeColor = active ? Primary : Color.White;
            button.FlatAppearance.BorderColor = active ? Primary : Secondary;
        }

        _alerts.Text = _data.Alerts.Count > 0 ? $"ALERTS ({_data.Alerts.Count})" : "ALERTS";
        // An alert nobody has looked at yet is worth colouring, even on an inactive tab.
        if (_data.Alerts.Count > 0 && _view != HereView.Alerts) _alerts.ForeColor = Alarm;

        _map.Visible = _view != HereView.Alerts;
        _alertList.Visible = _view == HereView.Alerts;

        if (_view == HereView.Alerts)
        {
            RenderAlerts();
            _status.Text = _data.Alerts.Count == 0
                ? "No emergency alerts received on this station."
                : $"{_data.Alerts.Count} alert(s). Amber alerts arrive as Safety or Rescue, hurricanes and storms as Weather.";
            return;
        }

        var set = _view == HereView.Traffic ? _data.Traffic : _data.Weather;
        SetMapImage(set is null ? null : Compose(set));
        _status.Text = DescribeStatus(set);
    }

    /// <summary>
    /// The footer line: how much of the map has arrived, when the station made it, and the
    /// ground it covers. On a station that sends nothing it says so rather than staying blank,
    /// because an empty window with no explanation reads as a broken feature.
    /// </summary>
    private static string DescribeStatus(HereImageSet? set)
    {
        if (set is null)
            return "Nothing received yet. Only stations carrying the HERE data service broadcast these maps.";

        var lines = set.Describe();
        if (set.TimeUtc != default) lines += $"   ·   {set.TimeUtc:yyyy-MM-dd HH:mm} UTC";
        if (set.Tiles.Count > 0)
            lines += $"\n{set.North:0.00}N {set.West:0.00}E  to  {set.South:0.00}N {set.East:0.00}E";
        return lines;
    }

    /// <summary>
    /// Swaps the displayed map and disposes the one it replaces. A composed mosaic is a large
    /// bitmap and these arrive continuously, so leaving them to the collector would show.
    /// </summary>
    private void SetMapImage(Image? image)
    {
        var previous = _map.Image;
        _map.Image = image;
        previous?.Dispose();
        _map.Invalidate();
    }

    /// <summary>
    /// Draws the placeholder when there is no map, so an empty frame still looks deliberate.
    /// </summary>
    private void MapOnPaint(object? sender, PaintEventArgs e)
    {
        if (_map.Image is not null) return;
        TextRenderer.DrawText(e.Graphics,
            _view == HereView.Traffic ? "NO TRAFFIC MAP" : "NO WEATHER MAP",
            PanelFonts.Placeholder, _map.ClientRectangle, Color.FromArgb(85, 105, 120),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// <summary>
    /// Lays the tiles out by the ground they cover rather than by their part number.
    /// Each tile carries its own corners, so the mosaic is built from geography and does
    /// not depend on guessing how a station numbers the nine pieces of a traffic map.
    /// </summary>
    internal static Bitmap? Compose(HereImageSet set)
    {
        var decoded = new List<(HereTile Tile, Bitmap Image)>();
        try
        {
            foreach (var tile in set.Tiles)
            {
                if (!tile.HasBounds) continue;
                var image = Decode(tile.Data);
                if (image is not null) decoded.Add((tile, image));
            }
            if (decoded.Count == 0) return null;

            var north = decoded.Max(d => d.Tile.North);
            var south = decoded.Min(d => d.Tile.South);
            var west = decoded.Min(d => d.Tile.West);
            var east = decoded.Max(d => d.Tile.East);

            // Scale comes from a tile rather than a fixed number, so the mosaic keeps the
            // resolution the station actually sent.
            var first = decoded[0];
            var perDegreeX = first.Image.Width / Math.Max(1e-6, first.Tile.East - first.Tile.West);
            var perDegreeY = first.Image.Height / Math.Max(1e-6, first.Tile.North - first.Tile.South);

            var width = (int)Math.Round((east - west) * perDegreeX);
            var height = (int)Math.Round((north - south) * perDegreeY);
            if (width is <= 0 or > 8192 || height is <= 0 or > 8192)
                return decoded.Count == 1 ? new Bitmap(first.Image) : null;

            var canvas = new Bitmap(width, height);
            using (var graphics = Graphics.FromImage(canvas))
            {
                graphics.Clear(Color.FromArgb(16, 23, 31));
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                foreach (var (tile, image) in decoded)
                {
                    var target = new Rectangle(
                        (int)Math.Round((tile.West - west) * perDegreeX),
                        (int)Math.Round((north - tile.North) * perDegreeY),
                        Math.Max(1, (int)Math.Round((tile.East - tile.West) * perDegreeX)),
                        Math.Max(1, (int)Math.Round((tile.North - tile.South) * perDegreeY)));
                    graphics.DrawImage(image, target);
                }
            }
            return canvas;
        }
        finally
        {
            foreach (var (_, image) in decoded) image.Dispose();
        }
    }

    /// <summary>
    /// Decodes one tile, refusing anything implausibly large. Tiles arrive from the air and
    /// can be corrupt; a bad one is skipped rather than allowed to fail the whole mosaic.
    /// </summary>
    private static Bitmap? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, false);
            using var image = Image.FromStream(stream, true, true);
            return image.Width > 8192 || image.Height > 8192 ? null : new Bitmap(image);
        }
        catch
        {
            // A truncated or unrecognised tile is skipped; the rest of the mosaic stands.
            return null;
        }
    }

    /// <summary>
    /// Rebuilds the alert list from scratch. There are at most a handful of alerts, so
    /// rebuilding is simpler and less error-prone than diffing, and the old controls are
    /// disposed rather than orphaned.
    /// </summary>
    private void RenderAlerts()
    {
        _alertList.SuspendLayout();
        foreach (Control existing in _alertList.Controls) existing.Dispose();
        _alertList.Controls.Clear();

        if (_data.Alerts.Count == 0)
        {
            _alertList.Controls.Add(new Label
            {
                Text = "No emergency alerts received on this station.",
                Dock = DockStyle.Top,
                Height = 40,
                ForeColor = Muted,
                Font = PanelFonts.Detail,
                Padding = new Padding(6)
            });
            _alertList.ResumeLayout();
            return;
        }

        // Added bottom-first because DockStyle.Top stacks in reverse insertion order.
        foreach (var alert in _data.Alerts.Reverse())
        {
            // Margin is ignored by a plain docked Panel, so the gap between cards is a
            // holder that paints the background rather than the card colour.
            var holder = new Panel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Background,
                Padding = new Padding(0, 0, 0, 8)
            };
            holder.Controls.Add(BuildAlertCard(alert));
            _alertList.Controls.Add(holder);
        }
        _alertList.ResumeLayout();
    }

    /// <summary>
    /// One alert: category and time, the text as broadcast, and the area it covers. The
    /// message is never truncated - an emergency alert is the one thing here worth reading
    /// in full.
    /// </summary>
    private static Control BuildAlertCard(HdAlert alert)
    {
        var card = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Card,
            Margin = new Padding(0, 0, 0, 8),
            Padding = new Padding(10, 8, 10, 10)
        };

        var locations = alert.DescribeLocations();
        if (locations.Length > 0)
            card.Controls.Add(new Label
            {
                Text = locations,
                Dock = DockStyle.Top,
                AutoSize = true,
                MaximumSize = new Size(760, 0),
                ForeColor = Muted,
                Font = PanelFonts.Small,
                Padding = new Padding(0, 6, 0, 0)
            });

        card.Controls.Add(new Label
        {
            Text = alert.Message,
            Dock = DockStyle.Top,
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            ForeColor = Color.White,
            Font = PanelFonts.Detail
        });

        card.Controls.Add(new Label
        {
            Text = $"{alert.Categories.ToUpperInvariant()}   ·   {alert.ReceivedUtc:HH:mm} UTC",
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = Alarm,
            Font = PanelFonts.Caption
        });

        return card;
    }

    /// <summary>
    /// A section button, styled to match the panel rather than the host theme.
    /// </summary>
    private static Button NewTab(string text)
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
            Margin = new Padding(3)
        };
        button.FlatAppearance.BorderColor = Secondary;
        return button;
    }
}
