using System.Collections.ObjectModel;
using System.Globalization;
using ScreenCord.Contracts;
using ScreenCord.Infrastructure;
using ScreenCord.Services;

namespace ScreenCord.ViewModels;

/// <summary>
/// Plano da conta, no cabeçalho, e a janela do ScreenCord Pro: vantagens, validade e compra de um período. O
/// pagamento acontece no navegador; o Pro é ativado quando a API confirma o pagamento e avisa pelo hub.
/// </summary>
public sealed class PlanViewModel : ObservableObject
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private readonly IPlanService _plans;
    private readonly IBrowserLauncher _browser;
    private readonly SynchronizationContext? _ui;
    private bool _isOpen;
    private bool _isBusy;
    private bool _isWaitingPayment;
    private string? _message;
    private string? _errorMessage;

    public PlanViewModel(IPlanService plans, IBrowserLauncher browser, IGroupsRealtime? realtime = null)
    {
        _plans = plans;
        _browser = browser;

        // Criado na thread da interface; as mudanças do plano chegam do hub, em outras threads.
        _ui = SynchronizationContext.Current;
        _plans.Changed += (_, _) => Post(OnPlanChanged);
        if (realtime is not null)
        {
            // Reconectado: um pagamento pode ter sido confirmado enquanto a conexão estava fora.
            realtime.StateChanged += (_, state) =>
            {
                if (state == RealtimeState.Connected)
                {
                    Post(() => _ = LoadAsync());
                }
            };
        }

        OpenCommand = new RelayCommand(() => IsOpen = true);
        CloseCommand = new RelayCommand(() => IsOpen = false);
        BuyCommand = new AsyncRelayCommand<ProOfferItem>(BuyAsync);
        Refresh();
    }

    public RelayCommand OpenCommand { get; }

    public RelayCommand CloseCommand { get; }

    public AsyncRelayCommand<ProOfferItem> BuyCommand { get; }

    public ObservableCollection<ProOfferItem> Offers { get; } = [];

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (SetProperty(ref _isOpen, value) && !value)
            {
                Message = null;
                ErrorMessage = null;
                _isWaitingPayment = false;
            }
        }
    }

    public bool IsPro => _plans.Current.Tier == PlanTier.Pro;

    public bool IsFree => !IsPro;

    /// <summary>"Pro até 16/11/2026", "Pro sem prazo" ou "Plano gratuito".</summary>
    public string StatusText => _plans.Current switch
    {
        { Tier: PlanTier.Pro, ProUntilUtc: { } until } => $"Seu Pro vale até {until.ToLocalTime():dd/MM/yyyy 'às' HH:mm}.",
        { Tier: PlanTier.Pro } => "Seu Pro não tem data para acabar.",
        _ => "Você está no plano gratuito.",
    };

    /// <summary>Título da lista de períodos: comprar com Pro ativo soma os dias ao fim do período atual.</summary>
    public string OffersHeader => IsPro ? "Adicionar mais tempo de Pro" : "Escolha um período";

    public bool HasOffers => Offers.Count > 0;

    public bool HasNoOffers => !HasOffers;

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>Aviso depois de abrir o pagamento ou quando o Pro é ativado.</summary>
    public string? Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => !string.IsNullOrEmpty(Message);

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

    /// <summary>Carrega o plano da conta. Falhas mantêm o que já se sabia (o gratuito, no começo).</summary>
    public async Task LoadAsync()
    {
        try
        {
            await _plans.RefreshAsync();
        }
        catch (Exception)
        {
            // Melhor esforço: a próxima reconexão ou o "Atualizar" tentam de novo.
        }
    }

    /// <summary>Conta desconectada: volta ao gratuito e fecha a janela.</summary>
    public void Clear()
    {
        IsOpen = false;
        _plans.Reset();
    }

    private async Task BuyAsync(ProOfferItem offer)
    {
        if (IsBusy)
        {
            return;
        }

        ErrorMessage = null;
        Message = null;
        IsBusy = true;
        try
        {
            var url = await _plans.StartCheckoutAsync(offer.Id);
            if (url is not null)
            {
                _browser.Open(url);
                _isWaitingPayment = true;
                Message = "A página de pagamento abriu no navegador. O Pro é ativado assim que o pagamento for confirmado, sem precisar reabrir o app.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ErrorMessages.For(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnPlanChanged()
    {
        Refresh();
        if (_isWaitingPayment && IsPro)
        {
            _isWaitingPayment = false;
            Message = "Pagamento confirmado. Obrigado por apoiar o ScreenCord!";
        }
    }

    private void Refresh()
    {
        Offers.Clear();
        foreach (var offer in _plans.Current.Offers)
        {
            Offers.Add(new ProOfferItem(offer.Id, offer.Name, offer.Days, (offer.PriceCents / 100m).ToString("C", PtBr)));
        }

        OnPropertyChanged(nameof(IsPro));
        OnPropertyChanged(nameof(IsFree));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(OffersHeader));
        OnPropertyChanged(nameof(HasOffers));
        OnPropertyChanged(nameof(HasNoOffers));
    }

    private void Post(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }
}

/// <param name="PriceText">"R$ 14,90".</param>
public sealed record ProOfferItem(string Id, string Name, int Days, string PriceText);
