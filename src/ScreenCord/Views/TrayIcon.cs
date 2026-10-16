using System.Windows;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ScreenCord.Views;

/// <summary>
/// Ícone do ScreenCord na bandeja do Windows. Clique abre a janela; o botão direito mostra o menu com o nome do
/// app (que também abre a janela) e "Sair".
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Drawing.Icon _icon;
    private readonly Drawing.Bitmap _menuImage;

    public TrayIcon(Action show, Action exit)
    {
        using (var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/screencord.ico")).Stream)
        {
            _icon = new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        }

        _menuImage = new Drawing.Icon(_icon, 16, 16).ToBitmap();

        _menu = new Forms.ContextMenuStrip();
        var title = new Forms.ToolStripMenuItem("ScreenCord", _menuImage, (_, _) => show());
        title.Font = new Drawing.Font(title.Font, Drawing.FontStyle.Bold);
        _menu.Items.Add(title);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(new Forms.ToolStripMenuItem("Sair", null, (_, _) => exit()));

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "ScreenCord",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                show();
            }
        };
    }

    public void Dispose()
    {
        // Sem esconder antes, o ícone fica "fantasma" na bandeja até o mouse passar por cima.
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _menuImage.Dispose();
        _icon.Dispose();
    }
}
