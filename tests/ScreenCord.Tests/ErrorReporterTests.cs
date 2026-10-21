using System.Net;
using System.Text.Json;
using ScreenCord.Contracts;
using ScreenCord.Infrastructure;
using ScreenCord.Services;
using ScreenCord.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ScreenCord.Tests;

/// <summary>Relatório de erros enviado à API para o painel de observabilidade.</summary>
public sealed class ErrorReporterTests : IDisposable
{
    private static readonly Guid InstallId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly StubHttpHandler _handler = new();
    private readonly ErrorReporter _reporter;
    private readonly ILogger _logger;

    public ErrorReporterTests()
    {
        // Intervalo longo: os testes mandam com FlushAsync.
        _reporter = new ErrorReporter(new Uri("http://localhost:5080/"), "Production", InstallId, _handler, TimeSpan.FromHours(1));
        _logger = new ErrorReporterLoggerProvider(_reporter).CreateLogger("ScreenCord.ViewModels.GroupsViewModel");
    }

    public void Dispose() => _reporter.Dispose();

    private ClientErrorReport SentReport(int index = 0) =>
        JsonSerializer.Deserialize<ClientErrorReport>(_handler.Requests[index].Body, JsonSerializerOptions.Web)!;

    [Fact]
    public async Task AvisosEErros_VaoEmLote_ComPilhaEVersao()
    {
        _handler.Enqueue(HttpStatusCode.Accepted);
        _logger.LogInformation("Só informação.");
        _logger.LogWarning("Conexão perdida ({Error}).", "timeout");
        _logger.LogError(new InvalidOperationException("quebrou"), "Falha ao entrar no grupo.");

        await _reporter.FlushAsync();

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("http://localhost:5080/telemetry/errors", request.Uri.AbsoluteUri);
        Assert.Equal(AppVersion.Current, request.Headers[ClientVersionContract.Header]);
        Assert.Null(request.Authorization);
        var report = SentReport();
        Assert.Equal(InstallId, report.InstallId);
        Assert.Equal(_reporter.RunId, report.RunId);
        Assert.Equal(["Warning", "Error"], report.Entries.Select(e => e.Level));
        Assert.Equal("Conexão perdida (timeout).", report.Entries[0].Message);
        Assert.Contains("System.InvalidOperationException: quebrou", report.Entries[1].Exception);
        Assert.Equal(0, _reporter.Pending);
    }

    [Fact]
    public async Task SemRede_GuardaEMandaDepois()
    {
        _handler.EnqueueException(new HttpRequestException("offline")).Enqueue(HttpStatusCode.ServiceUnavailable).Enqueue(HttpStatusCode.Accepted);
        _logger.LogError("Erro 1.");

        await _reporter.FlushAsync();
        await _reporter.FlushAsync();
        Assert.Equal(1, _reporter.Pending);
        await _reporter.FlushAsync();

        Assert.Equal(3, _handler.Requests.Count);
        Assert.Equal(0, _reporter.Pending);
        Assert.Equal("Erro 1.", Assert.Single(SentReport(2).Entries).Message);
    }

    [Fact]
    public async Task ErroRepetido_ViraNoMaximoCincoEntradas()
    {
        _handler.Enqueue(HttpStatusCode.Accepted);
        for (var i = 0; i < 40; i++)
        {
            _logger.LogWarning("Chamada ao hub falhou.");
        }

        await _reporter.FlushAsync();

        Assert.Equal(ErrorReporter.MaxRepeats, SentReport().Entries.Count);
    }

    [Fact]
    public async Task FalhaNaApi_LevaOTraceDaRequisicaoParaOErroEParaORelatorio()
    {
        var api = new StubHttpHandler();
        api.Enqueue(HttpStatusCode.OK, """{"token":"t","expiresAtUtc":"2026-11-12T12:00:00+00:00","user":null}""")
           .Enqueue(HttpStatusCode.InternalServerError);
        var client = new CentralApiClient(
            new StubHttpClientFactory(api),
            new FakeSessionManager { HasSession = true, AccessToken = "discord-access" },
            Options.Create(new ApiOptions { BaseUrl = "http://localhost:5080" }),
            NullLogger<CentralApiClient>.Instance);

        var error = await Assert.ThrowsAsync<DiscordException>(() => client.SendAsync<object>(HttpMethod.Get, "groups", null, CancellationToken.None));
        _handler.Enqueue(HttpStatusCode.Accepted);
        _logger.LogWarning("Operação falhou: {Kind} (HTTP {StatusCode}, trace {TraceId}).", error.Kind, (int?)error.StatusCode, error.TraceId);
        _logger.LogError(error, "Erro inesperado.");
        await _reporter.FlushAsync();

        var traceparent = api.Requests[1].Headers["traceparent"].Split('-');
        Assert.Equal(4, traceparent.Length);
        Assert.Equal(traceparent[1], error.TraceId);
        Assert.Equal(32, error.TraceId!.Length);
        Assert.All(SentReport().Entries, e => Assert.Equal(error.TraceId, e.TraceId));
        Assert.NotEqual(traceparent[1], api.Requests[0].Headers["traceparent"].Split('-')[1]);
    }

    [Fact]
    public void IdDaInstalacao_ECriadoUmaVezEReaproveitado()
    {
        var folder = Path.Combine(Path.GetTempPath(), "screencord-tests-" + Guid.NewGuid());
        try
        {
            var first = ErrorReporter.LoadInstallId(folder);

            Assert.NotEqual(Guid.Empty, first);
            Assert.Equal(first, ErrorReporter.LoadInstallId(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
