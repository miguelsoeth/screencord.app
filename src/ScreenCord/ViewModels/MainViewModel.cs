using ScreenCord.Infrastructure;
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
    private bool _isLoadingProfile;
    private bool _isConfirmingDisconnect;
    private UserProfile? _profile;
    private string? _errorMessage;

    public MainViewModel(
        ISessionManager session,
        IProfileService profileService,
        FriendsViewModel friends,
        GroupsViewModel groups,
        ILogger<MainViewModel> logger)
    {
        _session = session;
        _profileService = profileService;
        _logger = logger;
        Friends = friends;
        Groups = groups;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnected && !IsInitializing);
        CancelConnectCommand = new RelayCommand(() => _signInCancellation?.Cancel(), () => IsSigningIn);
        RefreshProfileCommand = new AsyncRelayCommand(RefreshProfileAsync, () => IsConnected && !IsLoadingProfile);
        DisconnectCommand = new RelayCommand(() => IsConfirmingDisconnect = true, () => IsConnected && !IsConfirmingDisconnect);
        ConfirmDisconnectCommand = new AsyncRelayCommand(ConfirmDisconnectAsync, () => IsConnected);
        CancelDisconnectCommand = new RelayCommand(() => IsConfirmingDisconnect = false, () => IsConfirmingDisconnect);
    }

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

    public bool IsLoadingProfile
    {
        get => _isLoadingProfile;
        private set
        {
            if (SetProperty(ref _isLoadingProfile, value))
            {
                OnStateChanged();
            }
        }
    }

    public bool ShowLogin => !IsInitializing && !IsConnected;

    public bool ShowConnectButton => ShowLogin && !IsSigningIn;

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

    private async Task ConfirmDisconnectAsync()
    {
        IsConfirmingDisconnect = false;

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
        IsLoadingProfile = true;
        try
        {
            Profile = await _profileService.GetProfileAsync();
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
        finally
        {
            IsLoadingProfile = false;
        }

        if (IsConnected)
        {
            await Task.WhenAll(Friends.LoadAsync(), Groups.LoadAsync());
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
        _ = Groups.SignOutAsync();
        Friends.Clear();
        IsConfirmingDisconnect = false;
        IsConnected = false;
    }

    private void OnStateChanged()
    {
        OnPropertyChanged(nameof(ShowLogin));
        OnPropertyChanged(nameof(ShowConnectButton));
        ConnectCommand.NotifyCanExecuteChanged();
        CancelConnectCommand.NotifyCanExecuteChanged();
        RefreshProfileCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        ConfirmDisconnectCommand.NotifyCanExecuteChanged();
        CancelDisconnectCommand.NotifyCanExecuteChanged();
    }
}
