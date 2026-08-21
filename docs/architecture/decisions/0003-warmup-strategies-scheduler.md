# warmup-strategies-scheduler

**Status**: Proposed
**Date**: 2026-08-21

## Context

A plataforma Foundry não provê warm pool nativo ("no warm pool to size"). A Stoke precisa manter agentes aquecidos para reduzir a latência de reativação, oferecendo duas estratégias plugáveis: pré-provisionamento de pool e keepalive. O keepalive precisa de um probe que gere atividade dentro da janela de idle, mas o schema de Invocations é definido pelo container do usuário, exigindo um probe fornecido pelo usuário além do ping genérico embutido.

Cada estratégia precisa de um scheduler que rode periodicamente (reabastecer pool, disparar probes antes do idle timeout). Esse loop precisa ser idiomático por linguagem, mas testável de forma determinística e, criticamente, **não bloqueante**: o loop e a primitiva de espera não podem prender uma thread.

## Priorities and Requirements (ordered)

1. **Não bloqueante (hard constraint)** — O loop de scheduling e a primitiva de delay MUST ser async/awaitable. Nunca bloquear uma thread (sem `Thread.Sleep`; Python usa `asyncio.sleep` via clock injetado; .NET usa delays async). Bloquear thread em serviço de longa duração degrada o host e a escalabilidade.
2. **Testabilidade determinística** — O tempo precisa ser injetável para testes não dependerem de wall-clock (sem `sleep` real em teste). Um clock/scheduler abstrato injetável permite avançar o tempo virtualmente.
3. **Idiomático por linguagem** — Python `asyncio` task; .NET `BackgroundService`/`IHostedService` + `PeriodicTimer`. A semântica pública é equivalente; a implementação é nativa (FR-021, FR-022).
4. **Probe plugável e genérico o suficiente** — Pré-provisionamento usa só o ciclo de vida (sem probe de dados); keepalive usa um `WarmupProbe` com ping genérico Responses opcional embutido + hook do usuário para Invocations/containers customizados (FR-013, FR-017).
5. **Dimensionável por definição de agente** — N sessões quentes por definição, tamanho-alvo independente por agente (FR-014, FR-015, CC-006).

## Options Considered

### Option 1: Estratégias plugáveis + probe abstrato + scheduler idiomático com clock injetável não bloqueante

Interface `WarmupStrategy` com duas implementações (`PreProvisionPoolStrategy`, `KeepaliveStrategy`). Abstração `WarmupProbe` (embutido `ResponsesPingProbe` + hook do usuário). O scheduler é idiomático (asyncio task / `BackgroundService`+`PeriodicTimer`) mas recebe um `Clock`/`Scheduler` injetável cuja primitiva de delay é async/awaitable. Em produção, o clock delega para o relógio real (delay async); em teste, um clock virtual avança o tempo deterministicamente.

**Evaluation against priorities**:
- **Não bloqueante**: Atende. A primitiva de delay do clock é `async`/awaitable em ambas as linguagens; nenhum `Thread.Sleep`.
- **Testabilidade determinística**: Atende. O clock injetado permite avançar o tempo sem esperar; testes rápidos e determinísticos.
- **Idiomático**: Atende. Cada linguagem usa seu mecanismo nativo de loop de fundo.
- **Probe plugável**: Atende. `WarmupProbe` isola o ping embutido e o hook do usuário.
- **Dimensionável**: Atende. O registry de pool mantém tamanho-alvo por definição de agente.

### Option 2: Scheduler idiomático usando o relógio real diretamente (sem abstração de clock)

Cada linguagem usa timer/loop nativo consultando o relógio do sistema diretamente, sem injeção de clock.

**Evaluation against priorities**:
- **Não bloqueante**: Atende (se usar delays async), mas frágil: sem a abstração, é fácil um contribuidor introduzir um `Thread.Sleep`/`Task.Delay` bloqueante sem barreira.
- **Testabilidade determinística**: Falha. Testes precisariam esperar tempo real ou recorrer a mocks frágeis; cenários de idle timeout (minutos) tornam-se lentos ou não determinísticos.
- **Idiomático**: Atende.
- **Probe plugável**: Atende (ortogonal).
- **Dimensionável**: Atende (ortogonal).

## Decision

Adotar a **Option 1**: duas estratégias plugáveis (`PreProvisionPoolStrategy`, `KeepaliveStrategy`) sob uma interface comum `WarmupStrategy`, abstração `WarmupProbe` (ping genérico Responses embutido opcional + hook do usuário), e um scheduler idiomático por linguagem (asyncio task no Python; `BackgroundService`/`IHostedService` + `PeriodicTimer` no .NET) com um **`Clock`/`Scheduler` injetável e estritamente não bloqueante**.

**Hard constraint (não bloqueante)**: o loop de scheduling e a primitiva de delay do clock MUST ser async/awaitable. É proibido bloquear thread: sem `Thread.Sleep` no .NET; Python usa `asyncio.sleep` através do clock injetado; .NET usa delays async (`Task.Delay`/`PeriodicTimer.WaitForNextTickAsync`) através do clock injetado. Em teste, o clock virtual avança o tempo sem espera real.

Justificativa ancorada nas prioridades: a Option 1 é a única que satisfaz a prioridade 2 (testabilidade determinística) e reforça a prioridade 1 (não bloqueante) com uma barreira explícita (a primitiva async vem do clock injetado). A Option 2 falha na testabilidade e enfraquece a garantia de não-bloqueio.

## Implementation Notes

- Contratos em `docs/features/stoke-beta/contracts/warmup-strategy.md`, `warmup-probe.md`, `clock-scheduler.md`.
- Registry de pool (tamanho-alvo por definição de agente, sessões rastreadas) em `docs/features/stoke-beta/data-model.md`.
- **Gap conhecido/assunção**: não está documentado se `create_session` sozinho reseta o idle timer. Se não resetar, o scheduler do pool reusa o mesmo `WarmupProbe` para renovar sessões do pool. Validar empiricamente (NEEDS RESEARCH em research.md).
- Observabilidade: spans `stoke.warmup.probe` e `stoke.warmup.refill` (ver plan.md, seção de observabilidade).

### Notas de segurança (security-review, 2026-08-21)

- **Amplificação de custo/taxa e abuso do scheduler (SEC-007)**: um loop de reconciliação/reabastecimento que, sob indisponibilidade do Foundry, tente recriar sessões sem limite pode martelar o serviço e inflar custo (cada `create_session`/probe é operação cobrada). Mitigações de design: limitar `targetSize` a um máximo **configurável e validado**; aplicar backoff exponencial com jitter e um teto de tentativas nas falhas de reconciliação; evitar tight loop quando o serviço está indisponível (alinhado ao edge case "tamanho-alvo não atingível" da spec); emitir a métrica de reabastecimento (`stoke.warmup.refill`) para detecção do padrão.

## References

* docs/features/stoke-beta/spec.md (FR-012..FR-017, CC-006, CC-007, SC-004)
* docs/features/stoke-beta/research.md (sem warm pool nativo; atividade que reseta idle timer)
* docs/features/stoke-beta/contracts/clock-scheduler.md
