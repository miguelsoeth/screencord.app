using System.Collections.ObjectModel;
using ScreenCord.Infrastructure;
using ScreenCord.Models;
using ScreenCord.Services;

namespace ScreenCord.ViewModels;

public sealed class FriendsViewModel : ObservableObject
{
    public const int MinSearchLength = 2;

    private readonly IFriendsService _friendsService;
    private bool _isLoading;
    private bool _hasLoaded;
    private bool _isSearching;
    private bool _showSearchResults;
    private string? _errorMessage;
    private string? _searchMessage;
    private string _searchText = "";
    private string _activeQuery = "";
    private bool _isOpen;
    private Person? _pendingRemoval;

    public FriendsViewModel(IFriendsService friendsService)
    {
        _friendsService = friendsService;

        SearchCommand = new AsyncRelayCommand(SearchAsync);
        ClearSearchCommand = new RelayCommand(ClearSearch);
        ReloadCommand = new AsyncRelayCommand(() => LoadAsync());
        AddCommand = new AsyncRelayCommand<Person>(p => RunActionAsync(() => _friendsService.SendRequestAsync(p.UserId)));
        AcceptCommand = new AsyncRelayCommand<Person>(p => RunActionAsync(() => _friendsService.AcceptRequestAsync(p.UserId)));
        DeclineCommand = new AsyncRelayCommand<Person>(p => RunActionAsync(() => _friendsService.DeleteRequestAsync(p.UserId)));
        RemoveCommand = new RelayCommand<Person>(p => PendingRemoval = p);
        ConfirmRemoveCommand = new AsyncRelayCommand(ConfirmRemoveAsync);
        CancelRemoveCommand = new RelayCommand(() => PendingRemoval = null);
    }

    public ObservableCollection<Person> Friends { get; } = [];

    public ObservableCollection<Person> IncomingRequests { get; } = [];

    public ObservableCollection<Person> OutgoingRequests { get; } = [];

    /// <summary>Usuários do sistema encontrados pela busca.</summary>
    public ObservableCollection<Person> SearchResults { get; } = [];

    public AsyncRelayCommand SearchCommand { get; }

    public RelayCommand ClearSearchCommand { get; }

    public AsyncRelayCommand ReloadCommand { get; }

    /// <summary>Envia uma solicitação de amizade.</summary>
    public AsyncRelayCommand<Person> AddCommand { get; }

    public AsyncRelayCommand<Person> AcceptCommand { get; }

    /// <summary>Recusa uma solicitação recebida ou cancela uma enviada.</summary>
    public AsyncRelayCommand<Person> DeclineCommand { get; }

    /// <summary>Pede a confirmação para desfazer a amizade; só <see cref="ConfirmRemoveCommand"/> remove de fato.</summary>
    public RelayCommand<Person> RemoveCommand { get; }

    public AsyncRelayCommand ConfirmRemoveCommand { get; }

    public RelayCommand CancelRemoveCommand { get; }

    /// <summary>Menu de amigos aberto (o botão de amigos no cabeçalho). Fechar descarta a confirmação pendente.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (SetProperty(ref _isOpen, value) && !value)
            {
                PendingRemoval = null;
            }
        }
    }

    /// <summary>Amigo aguardando a confirmação de remoção.</summary>
    public Person? PendingRemoval
    {
        get => _pendingRemoval;
        private set
        {
            if (SetProperty(ref _pendingRemoval, value))
            {
                OnPropertyChanged(nameof(HasPendingRemoval));
            }
        }
    }

    public bool HasPendingRemoval => PendingRemoval is not null;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set => SetProperty(ref _isSearching, value);
    }

    /// <summary>Verdadeiro quando a área de conteúdo mostra a busca em vez da lista de amigos.</summary>
    public bool ShowSearchResults
    {
        get => _showSearchResults;
        private set
        {
            if (SetProperty(ref _showSearchResults, value))
            {
                OnPropertyChanged(nameof(ShowFriends));
            }
        }
    }

    public bool ShowFriends => !ShowSearchResults;

    public bool HasIncomingRequests => IncomingRequests.Count > 0;

    public bool HasOutgoingRequests => OutgoingRequests.Count > 0;

    public bool HasFriends => Friends.Count > 0;

    /// <summary>Lista carregada com sucesso e sem amigos.</summary>
    public bool ShowNoFriends => _hasLoaded && Friends.Count == 0;

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

    /// <summary>Aviso da busca: termo curto ou nenhum resultado.</summary>
    public string? SearchMessage
    {
        get => _searchMessage;
        private set
        {
            if (SetProperty(ref _searchMessage, value))
            {
                OnPropertyChanged(nameof(HasSearchMessage));
            }
        }
    }

    public bool HasSearchMessage => !string.IsNullOrEmpty(SearchMessage);

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value ?? "");
    }

    /// <summary>Carrega amigos e solicitações. O primeiro acesso também registra o usuário na API central.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            await RefreshListsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ErrorMessages.For(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Encerra a sessão na API central e limpa a tela.</summary>
    public async Task SignOutAsync()
    {
        try
        {
            await _friendsService.SignOutAsync();
        }
        catch (Exception)
        {
            // Melhor esforço: os dados locais são limpos de qualquer forma.
        }

        Clear();
    }

    public void Clear()
    {
        _friendsService.ForgetSession();
        _hasLoaded = false;
        Replace(Friends, []);
        Replace(IncomingRequests, []);
        Replace(OutgoingRequests, []);
        ClearSearch();
        ErrorMessage = null;
        PendingRemoval = null;
        IsOpen = false;
        NotifyListState();
    }

    private async Task ConfirmRemoveAsync()
    {
        if (PendingRemoval is not { } person)
        {
            return;
        }

        PendingRemoval = null;
        await RunActionAsync(() => _friendsService.RemoveFriendAsync(person.UserId));
    }

    private async Task SearchAsync()
    {
        var query = SearchText.Trim().TrimStart('@').Trim();
        ErrorMessage = null;
        SearchResults.Clear();
        ShowSearchResults = true;

        if (query.Length < MinSearchLength)
        {
            _activeQuery = "";
            SearchMessage = $"Digite pelo menos {MinSearchLength} caracteres para buscar.";
            return;
        }

        _activeQuery = query;
        SearchMessage = null;
        IsSearching = true;
        try
        {
            await RunSearchAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ErrorMessages.For(ex);
        }
        finally
        {
            IsSearching = false;
        }
    }

    private void ClearSearch()
    {
        _activeQuery = "";
        SearchText = "";
        SearchResults.Clear();
        SearchMessage = null;
        ShowSearchResults = false;
    }

    /// <summary>Executa uma ação e recarrega tudo, para que cada item reflita a relação atual no servidor.</summary>
    private async Task RunActionAsync(Func<Task> action)
    {
        ErrorMessage = null;
        string? actionError = null;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            actionError = ErrorMessages.For(ex);
        }

        try
        {
            await RefreshListsAsync(CancellationToken.None);
            if (ShowSearchResults && _activeQuery.Length > 0)
            {
                await RunSearchAsync();
            }
        }
        catch (Exception ex)
        {
            actionError ??= ErrorMessages.For(ex);
        }

        ErrorMessage = actionError;
    }

    private async Task RunSearchAsync()
    {
        var results = await _friendsService.SearchUsersAsync(_activeQuery);
        Replace(SearchResults, results);
        SearchMessage = results.Count == 0
            ? $"Nenhum usuário do ScreenCord encontrado para \"{_activeQuery}\". A pessoa precisa ter entrado no sistema com o Discord."
            : null;
    }

    private async Task RefreshListsAsync(CancellationToken cancellationToken)
    {
        var friends = await _friendsService.GetFriendsAsync(cancellationToken);
        var requests = await _friendsService.GetRequestsAsync(cancellationToken);

        Replace(Friends, friends);
        Replace(IncomingRequests, requests.Incoming);
        Replace(OutgoingRequests, requests.Outgoing);
        _hasLoaded = true;
        NotifyListState();
    }

    private void NotifyListState()
    {
        OnPropertyChanged(nameof(HasFriends));
        OnPropertyChanged(nameof(ShowNoFriends));
        OnPropertyChanged(nameof(HasIncomingRequests));
        OnPropertyChanged(nameof(HasOutgoingRequests));
    }

    private static void Replace(ObservableCollection<Person> target, IReadOnlyList<Person> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
