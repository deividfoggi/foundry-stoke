# Contrato: WarmupStrategy

- **ADR**: 0003-warmup-strategies-scheduler
- **Requisitos**: FR-012, FR-013, FR-014, FR-015; CC-006, SC-004

Interface comum de estratégia de aquecimento, selecionável pelo usuário. Duas
implementações no beta: `PreProvisionPoolStrategy` e `KeepaliveStrategy`. O loop de
scheduling é não bloqueante e usa um `Clock`/`Scheduler` injetável (ver
clock-scheduler.md).

## Operações

```
interface WarmupStrategy:

    start(scheduler: Scheduler) -> void        # inicia o loop de aquecimento (não bloqueante)
    stop() -> void                             # encerra o loop de forma cooperativa
    reconcile() -> WarmupReport                # executa um ciclo de reconciliação (testável)
```

## PreProvisionPoolStrategy

- Mantém N sessões prontas por definição de agente (`targetSize` do `WarmPoolRegistry`).
- Ao consumir uma sessão do pool, reabastece até o tamanho-alvo (CC-006).
- Usa exclusivamente o ciclo de vida de sessão (`SessionController.createSession`); **não**
  usa probe de dados.
- Múltiplas definições de agente têm pools dimensionados independentemente (FR-015).

Parâmetros:

| Parâmetro | Tipo | Descrição |
|-----------|------|-----------|
| `agentDefinitionId` | string | Definição alvo. |
| `targetSize` | int >= 0 | N sessões quentes desejadas. |
| `refillInterval` | duração | Intervalo de reconciliação do pool (via Scheduler). |

## KeepaliveStrategy

- Executa um `WarmupProbe` (ver warmup-probe.md) dentro da janela de idle para manter a
  sessão utilizável (FR-013).
- Probe embutido opcional = ping genérico Responses; probe do usuário = hook para
  Invocations/containers customizados (FR-017; CC-007).

Parâmetros:

| Parâmetro | Tipo | Descrição |
|-----------|------|-----------|
| `probe` | WarmupProbe | Probe a executar (embutido ou fornecido pelo usuário). |
| `interval` | duração | Intervalo entre probes, dentro da janela de idle. |

## Erros / observabilidade

- Falha ao atingir o `targetSize` por indisponibilidade: registra e tenta novamente no
  próximo ciclo; não falha o processo (edge case da spec).
- Spans: `stoke.warmup.probe`, `stoke.warmup.refill` (ver plan.md).

## Notas idiomáticas

- Python: `asyncio` task para o loop; métodos `async`.
- .NET: `IWarmupStrategy` hospedada em `BackgroundService`/`IHostedService`;
  `PeriodicTimer` para o tick; delays sempre async (ADR 0003, hard constraint não
  bloqueante).
