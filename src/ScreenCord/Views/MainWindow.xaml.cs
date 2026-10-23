using ScreenCord.Localization;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using ScreenCord.Infrastructure;
using ScreenCord.ViewModels;

namespace ScreenCord.Views;

public partial class MainWindow : Window
{
    /// <summary>Largura da lateral de grupos mais o espaço entre ela e a tela.</summary>
    private const double SidebarSpace = 252 + 12;

    /// <summary>Espaço entre o cabeçalho e a tela.</summary>
    private const double StageGap = 12;

    /// <summary>Linha entre telas mostradas juntas.</summary>
    private const double PlayerGap = 2;

    private readonly MainViewModel _viewModel;
    private readonly StreamSlot[] _slots;
    private long _streamCounter;

    /// <summary>Colunas da grade quando há mais de uma tela; as linhas vêm do número de telas.</summary>
    private int _columns = 1;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _slots = [.. PlayerHost.Children.OfType<StreamPlayer>().Select(player => new StreamSlot(player))];
        foreach (var slot in _slots)
        {
            slot.Player.FullScreenChanged += (_, _) => UpdateVideoPaused();
        }

        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loc.LanguageChanged += () => OnWindowStateChanged(this, EventArgs.Empty);

        // Confirmação de sair/excluir grupo: foco no "Cancelar", para Enter não confirmar por acidente e Esc fechar.
        viewModel.Groups.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GroupsViewModel.IsConfirming) && viewModel.Groups.IsConfirming)
            {
                Dispatcher.BeginInvoke(() => CancelGroupConfirmButton.Focus(), System.Windows.Threading.DispatcherPriority.Input);
            }

            // Criar grupo: foco no nome ao abrir, para digitar direto, Enter criar e Esc cancelar.
            if (e.PropertyName == nameof(GroupsViewModel.IsCreating) && viewModel.Groups.IsCreating)
            {
                Dispatcher.BeginInvoke(() => NewGroupBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
            }
        };

        // Janela de amigos: foco na busca ao abrir, para digitar direto e o Esc fechar.
        viewModel.Friends.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FriendsViewModel.IsOpen) && viewModel.Friends.IsOpen)
            {
                Dispatcher.BeginInvoke(() => SearchBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
            }
        };
        // Sobre: foco no próprio overlay (sem o anel de foco num botão), para o Esc fechar.
        if (viewModel.About is { } about)
        {
            about.PropertyChanged += (_, e) =>
            {
                // Também depois do "Testar de novo": o botão fica desabilitado durante a checagem e o foco sai do overlay.
                if ((e.PropertyName == nameof(AboutViewModel.IsOpen) || e.PropertyName == nameof(AboutViewModel.ApiState)) &&
                    about.IsOpen && !AboutOverlay.IsKeyboardFocusWithin)
                {
                    Dispatcher.BeginInvoke(() => AboutOverlay.Focus(), System.Windows.Threading.DispatcherPriority.Input);
                }
            };
        }

        // Usar sem conta: foco no nome, para digitar direto e o Enter entrar.
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsLocalForm) && viewModel.IsLocalForm)
            {
                Dispatcher.BeginInvoke(() => LocalNameBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
            }
        };

        Loaded += async (_, _) => await viewModel.InitializeAsync();

        RootGrid.SizeChanged += (_, _) => UpdateStageSize();
        HeaderCard.SizeChanged += (_, _) => UpdateStageSize();
        ProfileError.SizeChanged += (_, _) => UpdateStageSize();
        ProfileError.IsVisibleChanged += (_, _) => UpdateStageSize();

        // Minimizada ou na bandeja, ninguém vê o vídeo: só o áudio continua, poupando a banda do servidor.
        StateChanged += (_, _) => UpdateVideoPaused();
        IsVisibleChanged += (_, _) => UpdateVideoPaused();
    }

    /// <summary>Fechar a janela só a esconde (o app continua na bandeja). Falso para fechar de verdade.</summary>
    public bool HidesOnClose { get; set; }

    private bool IsOutOfSight => WindowState == WindowState.Minimized || !IsVisible;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (HidesOnClose)
        {
            e.Cancel = true;
            foreach (var slot in _slots)
            {
                slot.Player.ExitFullScreen();
            }

            Hide();
        }

        base.OnClosing(e);
    }

    /// <summary>Mostra a janela (também quando está na bandeja ou minimizada) e a traz para a frente.</summary>
    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void UpdateVideoPaused()
    {
        // Com uma tela em tela cheia, as outras ficam cobertas: só o áudio delas continua, poupando a banda.
        var covered = _slots.Any(s => s.Player.HasFullScreenWindow);
        foreach (var slot in _slots)
        {
            if (slot.Viewer is { } viewer)
            {
                viewer.IsVideoPaused = IsOutOfSight || (covered && !slot.Player.HasFullScreenWindow);
            }
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    /// <summary>Clique na área esmaecida (fora do cartão) fecha a janela de amigos.</summary>
    private void OnFriendsOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender)
        {
            _viewModel.Friends.IsOpen = false;
        }
    }

    /// <summary>Clique na área esmaecida (fora do cartão) fecha o "Sobre".</summary>
    private void OnAboutOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender && _viewModel.About is { } about)
        {
            about.IsOpen = false;
        }
    }

    private void OnOpenLogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(FileLoggerProvider.DefaultFolder);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { FileLoggerProvider.DefaultFolder } })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Sem acesso à pasta ou ao Explorer: não há o que mostrar.
        }
    }

    /// <summary>Copia o resumo da versão e da conexão; o ícone vira um "certo" por um instante para confirmar.</summary>
    private async void OnCopyDetailsClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.About is not { } about)
        {
            return;
        }

        try
        {
            Clipboard.SetText(about.DetailsText);
        }
        catch (COMException)
        {
            // A área de transferência estava em uso por outro programa.
            return;
        }

        CopyDetailsIcon.Data = (System.Windows.Media.Geometry)FindResource("CheckIcon");
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        CopyDetailsIcon.Data = (System.Windows.Media.Geometry)FindResource("CopyIcon");
    }

    /// <summary>Copia o IP da sala local; o ícone vira um "certo" por um instante para confirmar.</summary>
    private async void OnCopyAddressClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string address } button ||
            button.Content is not System.Windows.Shapes.Path icon)
        {
            return;
        }

        try
        {
            Clipboard.SetText(address);
        }
        catch (COMException)
        {
            // A área de transferência estava em uso por outro programa; o IP continua selecionável.
            return;
        }

        icon.Data = (System.Windows.Media.Geometry)FindResource("CheckIcon");
        button.ToolTip = Strings.Copied;
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        icon.Data = (System.Windows.Media.Geometry)FindResource("CopyIcon");
        button.ToolTip = Strings.CopyIp;
    }

    /// <summary>Clique na área esmaecida (fora do cartão) cancela a criação do grupo.</summary>
    private void OnCreateGroupOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender)
        {
            _viewModel.Groups.CancelCreateCommand.Execute(null);
        }
    }

    /// <summary>
    /// Com a barra de título própria, a janela maximizada passa da tela pela espessura da borda redimensionável;
    /// a margem traz o conteúdo (e os botões da barra) de volta para a área visível.
    /// </summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        WindowRoot.Margin = maximized ? MaximizedOverflow() : new Thickness(0);
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? Strings.Restore : Strings.Maximize;
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, (string)MaximizeButton.ToolTip);
    }

    private Thickness MaximizedOverflow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0)
        {
            return new Thickness(8);
        }

        var scale = dpi / 96.0;
        var x = (GetSystemMetricsForDpi(SM_CXSIZEFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi)) / scale;
        var y = (GetSystemMetricsForDpi(SM_CYSIZEFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi)) / scale;
        return new Thickness(x, y, x, y);
    }

    private const int SM_CXSIZEFRAME = 32;
    private const int SM_CYSIZEFRAME = 33;
    private const int SM_CXPADDEDBORDER = 92;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    /// <summary>
    /// Dimensiona a área das telas no maior bloco que cabe ao lado da lateral e abaixo do cabeçalho. Uma tela é
    /// 16:9; com mais, o vídeo de cada uma é 16:9 numa grade cujo número de colunas é o que deixar o vídeo maior
    /// (duas telas lado a lado ou empilhadas, quatro em 2 × 2). O bloco todo fica centralizado na janela, e a
    /// lateral de grupos tem a mesma altura que ele.
    /// </summary>
    private void UpdateStageSize()
    {
        var margin = MainGrid.Margin;
        var width = RootGrid.ActualWidth - margin.Left - margin.Right - SidebarSpace;
        var above = HeaderCard.ActualHeight + (ProfileError.IsVisible ? ProfileError.ActualHeight + ProfileError.Margin.Top : 0);
        var height = RootGrid.ActualHeight - margin.Top - margin.Bottom - above - StageGap;
        if (width <= 0 || height <= 0)
        {
            ApplyPlayerLayout();
            return;
        }

        var count = Math.Max(1, _slots.Count(s => s.Viewer is not null));
        var bestVideo = 0.0;
        for (var columns = 1; columns <= count; columns++)
        {
            var rows = (count + columns - 1) / columns;
            var video = Math.Min((width - PlayerGap * (columns - 1)) / columns, (height - PlayerGap * (rows - 1)) / rows * 16 / 9);
            if (video > bestVideo)
            {
                bestVideo = video;
                _columns = columns;
            }
        }

        var videoWidth = Math.Floor(bestVideo);
        var videoHeight = Math.Floor(videoWidth * 9 / 16);
        var gridRows = (count + _columns - 1) / _columns;
        StageColumn.Width = new GridLength(videoWidth * _columns + PlayerGap * (_columns - 1));
        StageRow.Height = new GridLength(videoHeight * gridRows + PlayerGap * (gridRows - 1) + StageGap);
        ApplyPlayerLayout();
    }

    /// <summary>
    /// Distribui as telas abertas pela grade, da mais antiga para a mais nova, com <see cref="PlayerGap"/> entre
    /// elas. Se sobra lugar na última linha, a última tela ocupa o resto.
    /// </summary>
    private void ApplyPlayerLayout()
    {
        var open = _slots.Where(s => s.Viewer is not null).OrderBy(s => s.Order).ToList();
        var columns = Math.Max(1, open.Count < 2 ? 1 : _columns);
        var rows = Math.Max(1, (open.Count + columns - 1) / columns);
        if (PlayerHost.ColumnDefinitions.Count != columns || PlayerHost.RowDefinitions.Count != rows)
        {
            PlayerHost.ColumnDefinitions.Clear();
            PlayerHost.RowDefinitions.Clear();
            for (var i = 0; i < columns; i++)
            {
                PlayerHost.ColumnDefinitions.Add(new ColumnDefinition());
            }

            for (var i = 0; i < rows; i++)
            {
                PlayerHost.RowDefinitions.Add(new RowDefinition());
            }
        }

        for (var index = 0; index < open.Count; index++)
        {
            var player = open[index].Player;
            var row = index / columns;
            var column = index % columns;
            var span = index == open.Count - 1 ? columns - column : 1;
            Grid.SetRow(player, row);
            Grid.SetRowSpan(player, 1);
            Grid.SetColumn(player, column);
            Grid.SetColumnSpan(player, span);
            player.Margin = new Thickness(
                column > 0 ? PlayerGap / 2 : 0,
                row > 0 ? PlayerGap / 2 : 0,
                column + span < columns ? PlayerGap / 2 : 0,
                row < rows - 1 ? PlayerGap / 2 : 0);
        }
    }

    /// <summary>
    /// Mostra a tela de alguém na área principal, junto das que já estavam abertas (cabem quatro). Com todos os
    /// lugares ocupados, a mais antiga é fechada (e o <c>closed</c> dela, chamado). <paramref name="closed"/> desta é
    /// chamado quando ela sai da tela.
    /// </summary>
    public void ShowStream(StreamViewerViewModel viewer, Action closed)
    {
        var slot = _slots.FirstOrDefault(s => s.Viewer is null) ?? _slots.MinBy(s => s.Order)!;
        CloseStream(slot);
        slot.Viewer = viewer;
        slot.Closed = closed;
        slot.Order = ++_streamCounter;
        viewer.CloseRequested += OnStreamCloseRequested;
        UpdateVideoPaused();
        slot.Player.DataContext = viewer;
        UpdateStageSize();
        Motion.Hide(NoStreamPanel, MotionKind.Reveal);
        Motion.Show(slot.Player, MotionKind.Fade);
    }

    private void OnStreamCloseRequested(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (_slots.FirstOrDefault(s => s.Viewer == sender) is { } slot)
        {
            CloseStream(slot);
        }
    });

    private void CloseStream(StreamSlot slot)
    {
        if (slot.Viewer is not { } viewer)
        {
            return;
        }

        var closed = slot.Closed;
        viewer.CloseRequested -= OnStreamCloseRequested;
        slot.Viewer = null;
        slot.Closed = null;
        slot.Player.ExitFullScreen();
        slot.Player.DataContext = null;
        slot.Player.Visibility = Visibility.Collapsed;
        UpdateStageSize();
        if (_slots.All(s => s.Viewer is null))
        {
            Motion.Show(NoStreamPanel, MotionKind.Reveal);
        }

        closed?.Invoke();
    }

    /// <summary>Uma das posições onde uma tela pode ser mostrada.</summary>
    private sealed class StreamSlot(StreamPlayer player)
    {
        public StreamPlayer Player { get; } = player;

        public StreamViewerViewModel? Viewer { get; set; }

        public Action? Closed { get; set; }

        /// <summary>Quando a tela foi aberta (maior = mais recente); a menor sai quando falta lugar.</summary>
        public long Order { get; set; }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Foco no "Cancelar" ao abrir a confirmação: Enter não desconecta por acidente e Esc fecha.
        if (e.PropertyName == nameof(MainViewModel.IsConfirmingDisconnect) && _viewModel.IsConfirmingDisconnect)
        {
            Dispatcher.BeginInvoke(() => CancelDisconnectButton.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }

        // Depois do login no navegador, traz o aplicativo de volta para a frente.
        if (e.PropertyName == nameof(MainViewModel.IsConnected) && _viewModel.IsConnected)
        {
            BringToFront();
        }
    }
}
