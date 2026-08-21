# Contrato: Clock / Scheduler (não bloqueante)

- **ADR**: 0003-warmup-strategies-scheduler
- **Requisitos**: FR-021, FR-022 (equivalência); suporte a SC-004 (testabilidade)

Abstração de tempo injetável usada pelos schedulers de warm-up. Existe para (1) garantir
que toda espera seja **não bloqueante** (async/awaitable) e (2) permitir testes
determinísticos avançando o tempo virtualmente, sem `sleep` real.

## Operações

```
interface Clock:

    now() -> timestamp                         # instante atual (UTC)
    delay(duration) -> awaitable<void>         # espera NÃO bloqueante; nunca prende thread

interface Scheduler:

    schedulePeriodic(interval, callback) -> Cancellation
        # Executa callback a cada intervalo, usando delay() não bloqueante do Clock.
    cancel(handle) -> void
```

## Hard constraint (não bloqueante)

- `delay(...)` MUST ser async/awaitable. É **proibido** bloquear thread:
  - Python: implementado sobre `asyncio.sleep` (via clock injetado). Nunca `time.sleep`.
  - .NET: implementado sobre `Task.Delay` / `PeriodicTimer.WaitForNextTickAsync`. Nunca
    `Thread.Sleep`.
- O loop de scheduling roda como task async (Python) ou `BackgroundService`/`PeriodicTimer`
  (.NET), sempre aguardando `delay()`.

## Implementações

- **SystemClock**: delega para o relógio real; `delay` = delay async real. Uso em produção.
- **VirtualClock** (teste): `now()` controlado; `delay()` completa quando o tempo virtual é
  avançado pelo teste. Permite exercitar janelas de idle (minutos) instantaneamente e de
  forma determinística (SC-004, CC-006, CC-007).

## Notas idiomáticas

- Python: `Clock` como `Protocol`; `delay` retorna coroutine; `Scheduler` gerencia
  `asyncio.Task`.
- .NET: `IClock`/`IScheduler`; `delay` retorna `Task`; integra com `CancellationToken` e
  `PeriodicTimer`.
