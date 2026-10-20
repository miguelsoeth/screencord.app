using ScreenCord.Localization;
using ScreenCord.Infrastructure;
using ScreenCord.Local;
using ScreenCord.Models;
using ScreenCord.Security;
using ScreenCord.Services;
using Microsoft.Extensions.Logging;

namespace ScreenCord.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly ISessionManager _session;
    private readonly IProfileService _profileService;
    private readonly ILogger<MainViewModel> _logger;

    private CancellationTokenSource? _signInCancellation;
    private bool _isInitializing = true;
    private bool _isConnected;
    private bool _isSigningIn;
    private bool _isRefreshing;
    private bool _isConfirmingDisconnect;
    private UserProfile? _profile;
    private string? _errorMessage;
    private readonly ILocalRooms? _localRooms;
    private readonly ILocalIdentityStore? _localIdentity;
    private readonly IBrowserLauncher? _browser;
    private bool _isLocalSession;
    private bool _isLocalForm;
    private string _localName = "";

    public MainViewModel(
        ISessionManager session,
        IProfileService profileService,
        FriendsViewModel friends,
        GroupsViewModel groups,
        ILogger<MainViewModel> logger,
        ILocalRooms? localRooms = null,
        ILocalIdentityStore? localIdentity = null,
        IBrowserLauncher? browser = null)
    {
        _browser = browser;
        _localRooms = localRooms;
        _localIdentity = localIdentity;
        _session = session;
        _profileService = profileService;
        _logger = logger;
        Friends = friends;
        Groups = groups;
        groups.InviteRequested += friends.OpenInvite;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnected && !IsInitializing);
        CancelConnectCommand = new RelayCommand(() => _signInCancellation?.Cancel(), () => IsSigningIn);
        RefreshProfileCommand = new AsyncRelayCommand(RefreshProfileAsync, () => IsConnected && !IsRefreshing);
        DisconnectCommand = new RelayCommand(() => IsConfirmingDisconnect = true, () => IsConnected && !IsConfirmingDisconnect);
        ConfirmDisconnectCommand = new AsyncRelayCommand(ConfirmDisconnectAsync, () => IsConnected);
        CancelDisconnectCommand = new RelayCommand(() => IsConfirmingDisconnect = false, () => IsConfirmingDisconnect);
        StartLocalCommand = new RelayCommand(StartLocal, () => ShowConnectButton && CanUseLocal);
        CancelLocalCommand = new RelayCommand(() => IsLocalForm = false);
        ConfirmLocalCommand = new AsyncRelayCommand(ConfirmLocalAsync);
        OpenSupportCommand = new RelayCommand(OpenSupport, () => _browser is not null);
    }

    /// <summary>Página de apoio ao projeto no Ko-fi.</summary>
    public static readonly Uri SupportUrl = new("https://ko-fi.com/miguelsoeth");

    /// <summary>Abre a página do Ko-fi no navegador (no login e no cabeçalho da tela principal).</summary>
    public RelayCommand OpenSupportCommand { get; }

    private void OpenSupport()
    {
        try
        {
            _browser?.Open(SupportUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Falha ao abrir a página de apoio ({ExceptionType}).", ex.GetType().Name);
        }
    }

    /// <summary>Abre o "Usar sem conta": só o nome, e o app entra nas salas locais.</summary>
    public RelayCommand StartLocalCommand { get; }

    public RelayCommand CancelLocalCommand { get; }

    public AsyncRelayCommand ConfirmLocalCommand { get; }

    public bool CanUseLocal => _localRooms is not null && _localIdentity is not null;

    /// <summary>Em uso sem conta do Discord: só as salas locais, sem amigos nem grupos da API.</summary>
    public bool IsLocalSession
    {
        get => _isLocalSession;
        private set
        {
            if (SetProperty(ref _isLocalSession, value))
            {
                OnPropertyChanged(nameof(IsDiscordSession));
                OnPropertyChanged(nameof(AccountStatusText));
            }
        }
    }

    public bool IsDiscordSession => !IsLocalSession;

    public string AccountStatusText => IsLocalSession ? Strings.NoAccountLocalOnly : Strings.AccountConnected;

    /// <summary>A tela inicial mostra o campo de nome do "Usar sem conta".</summary>
    public bool IsLocalForm
    {
        get => _isLocalForm;
        private set
        {
            if (SetProperty(ref _isLocalForm, value))
            {
                OnPropertyChanged(nameof(ShowConnectButton));
                OnPropertyChanged(nameof(ShowLocalLink));
                StartLocalCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool ShowLocalLink => ShowConnectButton && CanUseLocal;

    public string LocalName
    {
        get => _localName;
        set => SetProperty(ref _localName, value ?? "");
    }

    public static int MaxLocalNameLength => LocalMember.MaxNameLength;

    public FriendsViewModel Friends { get; }

    /// <summary>Grupos de presença em tempo real, na lateral direita.</summary>
    public GroupsViewModel Groups { get; }

    public AsyncRelayCommand ConnectCommand { get; }

    public RelayCommand CancelConnectCommand { get; }

    public AsyncRelayCommand RefreshProfileCommand { get; }

    /// <summary>Abre a confirmação de desconexão.</summary>
    public RelayCommand DisconnectCommand { get; }

    /// <summary>Desconecta de fato, depois da confirmação.</summary>
    public AsyncRelayCommand ConfirmDisconnectCommand { get; }

    public RelayCommand CancelDisconnectCommand { get; }

    /// <summary>Verdadeiro enquanto a janela de confirmação de desconexão está aberta.</summary>
    public bool IsConfirmingDisconnect
    {
        get => _isConfirmingDisconnect;
        private set
        {
            if (SetProperty(ref _isConfirmingDisconnect, value))
            {
                OnPropertyChanged(nameof(IsMainInteractive));
                OnStateChanged();
            }
        }
    }

    public bool IsMainInteractive => !IsConfirmingDisconnect;

    /// <summary>Verdadeiro enquanto a sessão salva é verificada na abertura do aplicativo.</summary>
    public bool IsInitializing
    {
        get => _isInitializing;
        private set
        {
            if (SetProperty(ref _isInitializing, value))
            {
                OnStateChanged();
            }
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnStateChanged();
            }
        }
    }

    /// <summary>Verdadeiro enquanto o login aguarda a autorização no navegador.</summary>
    public bool IsSigningIn
    {
        get => _isSigningIn;
        private set
        {
            if (SetProperty(ref _isSigningIn, value))
            {
                OnStateChanged();
            }
        }
    }

    /// <summary>Verdadeiro enquanto perfil, amigos e grupos são carregados (o ícone de atualizar gira).</summary>
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                OnStateChanged();
            }
        }
    }

    public bool ShowLogin => !IsInitializing && !IsConnected;

    public bool ShowConnectButton => ShowLogin && !IsSigningIn && !IsLocalForm;

    public UserProfile? Profile
    {
        get => _profile;
        private set
        {
            if (SetProperty(ref _profile, value))
            {
                OnPropertyChanged(nameof(HasProfile));
            }
        }
    }

    public bool HasProfile => Profile is not null;

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>Reutiliza a sessão salva, se houver, sem pedir login novamente.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            if (await _session.HasSessionAsync())
            {
                IsConnected = true;
                IsInitializing = false;
                await LoadConnectedDataAsync();
            }
            else if (_localIdentity?.Load() is { SignedIn: true, Name: { } name })
            {
                // Usado sem conta da última vez: volta direto para as salas locais, sem depender da API.
                EnterLocalSession(name);
                IsInitializing = false;
                await LoadConnectedDataAsync();
            }
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
        finally
        {
            IsInitializing = false;
        }
    }

    private async Task ConnectAsync()
    {
        ErrorMessage = null;
        using var cancellation = new CancellationTokenSource();
        _signInCancellation = cancellation;
        IsSigningIn = true;

        try
        {
            await _session.SignInAsync(cancellation.Token);
            IsConnected = true;
        }
        catch (OperationCanceledException)
        {
            // Cancelado pelo usuário: volta à tela inicial sem mensagem de erro.
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
        finally
        {
            _signInCancellation = null;
            IsSigningIn = false;
        }

        if (IsConnected)
        {
            await LoadConnectedDataAsync();
        }
    }

    private Task RefreshProfileAsync() => LoadConnectedDataAsync();

    private void StartLocal()
    {
        ErrorMessage = null;
        LocalName = _localIdentity?.Load().Name ?? "";
        IsLocalForm = true;
    }

    private async Task ConfirmLocalAsync()
    {
        if (_localIdentity is null || LocalIdentityStore.Clean(LocalName) is not { } name)
        {
            ErrorMessage = Strings.EnterLocalName;
            return;
        }

        ErrorMessage = null;
        _localIdentity.Save(_localIdentity.Load() with { Name = name, SignedIn = true });
        IsLocalForm = false;
        EnterLocalSession(name);
        await LoadConnectedDataAsync();
    }

    private void EnterLocalSession(string name)
    {
        _localRooms!.IsLocalOnly = true;
        _localRooms.SetIdentity(name);
        var avatar = AvatarUrlBuilder.Build(new DiscordUser { Id = "0" }, size: 128);
        Profile = new UserProfile(_localRooms.Me.Id.ToString(), name, name, null, avatar, HasCustomAvatar: false);
        IsLocalSession = true;
        IsConnected = true;
    }

    private async Task ConfirmDisconnectAsync()
    {
        IsConfirmingDisconnect = false;
        if (IsLocalSession)
        {
            await Groups.SignOutAsync();
            if (_localIdentity is not null)
            {
                _localIdentity.Save(_localIdentity.Load() with { SignedIn = false });
            }

            ResetToSignedOut();
            ErrorMessage = null;
            return;
        }

        // A conexão em tempo real fecha antes, e a sessão da API central é encerrada enquanto ainda há um token para identificá-la.
        await Groups.SignOutAsync();
        await Friends.SignOutAsync();

        try
        {
            await _session.SignOutAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Falha ao encerrar a sessão ({ExceptionType}).", ex.GetType().Name);
        }

        ResetToSignedOut();
        ErrorMessage = null;
    }

    private async Task LoadConnectedDataAsync()
    {
        ErrorMessage = null;
        IsRefreshing = true;
        try
        {
            if (IsLocalSession)
            {
                await Groups.LoadAsync();
                return;
            }

            try
            {
                Profile = await _profileService.GetProfileAsync();
                // Nas salas locais, quem tem conta aparece com o nome e a foto do Discord.
                _localRooms?.SetIdentity(Profile.DisplayName, Profile.Id, Profile.AvatarHash);
            }
            catch (Exception ex)
            {
                HandleError(ex);
            }

            if (IsConnected)
            {
                await Task.WhenAll(Friends.LoadAsync(), Groups.LoadAsync());
            }
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void HandleError(Exception exception)
    {
        if (exception is DiscordException discord)
        {
            // Somente o tipo do erro vai para o log; mensagens e corpos de resposta ficam de fora.
            _logger.LogWarning("Operação falhou: {Kind} (HTTP {StatusCode}).", discord.Kind, (int?)discord.StatusCode);
            if (discord.Kind == DiscordErrorKind.Unauthorized)
            {
                ResetToSignedOut();
            }
        }
        else
        {
            _logger.LogError(exception, "Erro inesperado.");
        }

        ErrorMessage = ErrorMessages.For(exception);
    }

    private void ResetToSignedOut()
    {
        Profile = null;
        IsLocalSession = false;
        if (_localRooms is not null)
        {
            _localRooms.IsLocalOnly = false;
        }

        _ = Groups.SignOutAsync();
        Friends.Clear();
        IsConfirmingDisconnect = false;
        IsConnected = false;
    }

    private void OnStateChanged()
    {
        OnPropertyChanged(nameof(ShowLogin));
        OnPropertyChanged(nameof(ShowConnectButton));
        OnPropertyChanged(nameof(ShowLocalLink));
        StartLocalCommand.NotifyCanExecuteChanged();
        ConnectCommand.NotifyCanExecuteChanged();
        CancelConnectCommand.NotifyCanExecuteChanged();
        RefreshProfileCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        ConfirmDisconnectCommand.NotifyCanExecuteChanged();
        CancelDisconnectCommand.NotifyCanExecuteChanged();
    }
}
