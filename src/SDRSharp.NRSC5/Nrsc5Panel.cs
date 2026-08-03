using System.Drawing;
using System.Windows.Forms;
using SDRSharp.Common;

namespace SDRSharp.NRSC5;

internal sealed class Nrsc5Panel : UserControl
{
    private readonly Nrsc5Engine _engine;
    private readonly CheckBox _enabled = new() { Text = "Decodificar HD Radio", AutoSize = true };
    private readonly CheckBox _replaceAudio = new() { Text = "Usar audio HD al sincronizar", AutoSize = true, Checked = true };
    private readonly ComboBox _program = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly Label _state = NewValueLabel();
    private readonly Label _station = NewValueLabel();
    private readonly Label _quality = NewValueLabel();
    private readonly Label _title = NewValueLabel();
    private readonly Label _artist = NewValueLabel();

    public Nrsc5Panel(ISharpControl control, Nrsc5Engine engine)
    {
        _engine = engine;
        AutoSize = true;
        Padding = new Padding(8);
        BackColor = control.ThemePanelColor;
        ForeColor = control.ThemeForeColor;

        for (var i = 1; i <= 8; i++) _program.Items.Add($"HD{i}");
        _program.SelectedIndex = 0;

        var restart = new Button { Text = "Reiniciar decodificador", AutoSize = true };
        var grid = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 9,
            Padding = new Padding(2)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(grid, 0, "Estado", _state);
        AddRow(grid, 1, "Estacion", _station);
        AddRow(grid, 2, "Calidad", _quality);
        AddRow(grid, 3, "Titulo", _title);
        AddRow(grid, 4, "Artista", _artist);
        AddRow(grid, 5, "Subcanal", _program);
        grid.Controls.Add(_enabled, 0, 6);
        grid.SetColumnSpan(_enabled, 2);
        grid.Controls.Add(_replaceAudio, 0, 7);
        grid.SetColumnSpan(_replaceAudio, 2);
        grid.Controls.Add(restart, 0, 8);
        grid.SetColumnSpan(restart, 2);
        Controls.Add(grid);

        _enabled.CheckedChanged += (_, _) => _engine.Enabled = _enabled.Checked;
        _replaceAudio.CheckedChanged += (_, _) => _engine.ReplaceAnalogAudio = _replaceAudio.Checked;
        _program.SelectedIndexChanged += (_, _) => _engine.SelectedProgram = Math.Max(0, _program.SelectedIndex);
        restart.Click += (_, _) => _engine.Restart();
        _engine.StatusChanged += EngineOnStatusChanged;
        EngineOnStatusChanged(_engine.Status);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _engine.StatusChanged -= EngineOnStatusChanged;
        base.Dispose(disposing);
    }

    private void EngineOnStatusChanged(Nrsc5Status status)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => EngineOnStatusChanged(status)));
            return;
        }

        _state.Text = status.Message;
        _station.Text = string.IsNullOrWhiteSpace(status.Station) ? "--" : status.Station;
        var iq = $"IQ {status.InputRate / 1000:0.0} kS/s | VFO {status.OffsetHz / 1000:+0.0;-0.0;0.0} kHz";
        _quality.Text = status.Synced
            ? iq + $" | MER {status.MerLower:0.0}/{status.MerUpper:0.0} dB | BER {status.Ber:0.0000}"
            : iq;
        _title.Text = string.IsNullOrWhiteSpace(status.Title) ? "--" : status.Title;
        _artist.Text = string.IsNullOrWhiteSpace(status.Artist) ? "--" : status.Artist;
        _state.ForeColor = status.Synced ? Color.MediumSeaGreen : ForeColor;
    }

    private static Label NewValueLabel() => new() { AutoSize = true, MaximumSize = new Size(280, 0), Text = "--" };

    private static void AddRow(TableLayoutPanel grid, int row, string name, Control value)
    {
        grid.Controls.Add(new Label { Text = name + ":", AutoSize = true, Margin = new Padding(0, 4, 8, 4) }, 0, row);
        grid.Controls.Add(value, 1, row);
    }
}
