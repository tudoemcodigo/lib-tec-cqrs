using Microsoft.Extensions.Hosting;
using TEC.Cqrs.Internal;

namespace TEC.Cqrs.AspNetCore.Internal;

/// <summary>
/// Executa na subida do host as verificações do TEC.Cqrs que dependem de registros feitos depois do <c>AddTecCqrs</c>
/// (commands sem validador, com <c>CqrsOptions.RequireValidatorForCommands</c>): a aplicação não sobe, em vez de falhar no
/// primeiro uso do command em produção.
/// </summary>
internal sealed class CqrsStartupValidationService(ValidatorStartupCheck validators) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        validators.Run();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
