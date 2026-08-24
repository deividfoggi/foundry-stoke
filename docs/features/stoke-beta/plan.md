# Plano de Implementação: Stoke Beta

- **Criado em**: 2026-08-21
- **Status**: Finalizado (decisões fechadas; ADRs 0001-0005 Proposed)
- **Spec base**: docs/features/stoke-beta/spec.md (v1.2)
- **Research**: docs/features/stoke-beta/research.md
- **Data model**: docs/features/stoke-beta/data-model.md
- **Contratos**: docs/features/stoke-beta/contracts/
- **ADRs**: 0001-durable-store-provider, 0002-control-plane-boundary, 0003-warmup-strategies-scheduler, 0004-cross-language-api-consistency, 0005-authentication-strategy, 0006-secrets-telemetry-redaction, 0007-pluggable-provider-trust-model (todos Proposed)

> Escopo do beta: **Python + .NET**. Go adiado até existir SDK oficial do Foundry para Go
> (research confirmou apenas a REST `/sessions`, sem SDK). O contrato cross-language é
> projetado para admitir Go depois sem quebra (ADR 0004).

## Visão Geral da Arquitetura

A Stoke é uma biblioteca de **control-plane** para instâncias de agentes hospedados no
Foundry. Ela não é um cliente de data-plane: o tráfego real de conversa/negócio continua
sendo enviado pela aplicação via SDK oficial do Foundry. A Stoke cobre cinco capacidades:

1. Store durável via provider desacoplado (modelo compatível com Cosmos, sem acoplamento).
2. Aquecimento de sessões (pré-provisionamento de pool e keepalive por probe plugável).
3. Controle de sessão/estado (status oficial `AgentSessionStatus` + `UNKNOWN`, retomada derivada, idle timeout 5-60 min).
4. Integração com o Foundry no control-plane de sessão (criar/referenciar/retomar/consultar).
5. Consistência de API semanticamente equivalente entre Python e .NET (Go adiado).

## Fronteiras de Componente: Control-plane vs Data-plane

| Responsabilidade | Dono | Observação |
|------------------|------|------------|
| Ciclo de vida de sessão (criar/consultar/parar/encerrar) | **Stoke** | API `/sessions` oficial |
| Warm-up (pool + keepalive) | **Stoke** | Plataforma não provê warm pool nativo |
| Store durável (estado da Stoke) | **Stoke** (via provider) | Núcleo agnóstico de tecnologia |
| Autenticação de control-plane | **Stoke** | DefaultAzureCredential + fallback |
| Observabilidade do control-plane | **Stoke** | OpenTelemetry |
| Tráfego de dados (Responses/Invocations de payload de app) | **Aplicação** | SDK oficial do Foundry |
| Probe de keepalive específico (Invocations/container) | **Aplicação** | Hook fornecido à Stoke |
| Persistência de `$HOME`/`/files` | **Plataforma Foundry** | Stoke nunca replica |

## Layout de Módulos por Linguagem

Monorepo, implementações isoladas por linguagem (sem compartilhar build/fonte). Beta =
`python/` + `dotnet/`. `go/` reservado/adiado (ADR 0004).

| Conceito | `python/` (`foundry_stoke`) | `dotnet/` (`Foundry.Stoke`) |
|----------|------------------------------|------------------------------|
| Cliente/entrypoint | `StokeClient` | `StokeClient` |
| Store durável (contrato) | `DurableStoreProvider` (Protocol/ABC) | `IDurableStoreProvider` |
| Providers de referência | `InMemoryStore`, `FileSystemStore` | `InMemoryStore`, `FileSystemStore` |
| Warm-up (contrato) | `WarmupStrategy` | `IWarmupStrategy` |
| Estratégias | `KeepaliveStrategy`, `PreProvisionPoolStrategy` | idem |
| Probe | `WarmupProbe` (+ `ResponsesPingProbe`) | `IWarmupProbe` (+ `ResponsesPingProbe`) |
| Controle de sessão | `SessionController` (típado via `azure-ai-projects`) | `ISessionController` (REST fallback via `Azure.Core`) |
| Auth | `CredentialProvider` | `ICredentialProvider` |
| Clock/Scheduler | `Clock`/`Scheduler` (asyncio) | `IClock`/`IScheduler` (`PeriodicTimer`) |

> Go: **adiado** (ADR 0004). Adicionar Go depois = implementar as mesmas interfaces e um
> harness sobre as fixtures de conformidade, sem quebrar Python/.NET.

## Contrato de API Cross-Language

Superfície pública semanticamente equivalente, idiomática por linguagem (FR-021, FR-022):
Python async (sync onde fizer sentido), .NET Task-based. Go adiado (ADR 0004). Os contratos
detalhados vivem em `contracts/`: [durable-store-provider](contracts/durable-store-provider.md),
[session-controller](contracts/session-controller.md), [warmup-strategy](contracts/warmup-strategy.md),
[warmup-probe](contracts/warmup-probe.md), [credential-provider](contracts/credential-provider.md),
[clock-scheduler](contracts/clock-scheduler.md). A equivalência é verificada pela suíte de
conformidade por fixtures (ADR 0004).

## Design de Warm-up + Abstração de Probe

- **Pré-provisionamento (pool)**: cria N sessões prontas por definição de agente via
  control-plane (`create_session`), reabastece ao consumir; tamanho-alvo dimensionável por
  definição (FR-014, FR-015). Não usa probe de dados.
- **Keepalive**: executa um `WarmupProbe` dentro da janela de idle (FR-013). Probe embutido
  opcional = ping genérico Responses; probe do usuário = callback/delegate para
  Invocations/containers customizados (FR-017).
- **Scheduler do pool**: idiomático por linguagem (asyncio task no Python; `BackgroundService`/
  `IHostedService` + `PeriodicTimer` no .NET) com um `Clock`/`Scheduler` injetável (ADR 0003).

### Restrição dura: scheduler NÃO bloqueante

O loop de scheduling e a primitiva de delay do clock MUST ser async/awaitable. É **proibido
bloquear thread**: sem `Thread.Sleep` no .NET; Python usa `asyncio.sleep` através do clock
injetado; .NET usa delays async (`Task.Delay`/`PeriodicTimer.WaitForNextTickAsync`) através
do clock injetado. Em teste, um `VirtualClock` avança o tempo deterministicamente, sem espera
real (ADR 0003; contrato clock-scheduler.md).

## Contrato de Provider de Store Durável

Interface pública agnóstica de tecnologia; núcleo sem código/dependência de Cosmos
(FR-006, FR-009, invariante). Modelo por registro: `id` estável + `partition key` +
`etag`/versão + `type` discriminador + payload JSON. Semântica: CRUD mínimo +
query-por-partição (FR-007, FR-008). Providers de referência: InMemory e FileSystem/JSON
(FR-010). Detalhes do modelo em data-model.md; contrato em contracts/durable-store-provider.md
(ADR 0001).

**File-locking (Q6, ADR 0001)**: o provider FileSystem/JSON usa **advisory file lock
cross-process** (`fcntl.flock`/`msvcrt.locking` no Python; `FileStream` com
`FileShare.None`/lock no .NET) além da concorrência otimista por etag. Portabilidade:
advisory locks não são garantidos em NFS/SMB; provider destinado a dev local, não produção.

## Camada de Autenticação

`DefaultAzureCredential` primário (Entra ID) com fallback por API key/connection string
quando a credencial primária não estiver disponível; erro claro quando nenhuma existir
(FR-019, FR-020, CC-005). Encapsulado por `CredentialProvider` (ADR 0005;
contracts/credential-provider.md). A mesma credencial é reusada pelo `SessionController`,
inclusive no caminho REST fallback do .NET.

## Observabilidade

Telemetria via OpenTelemetry, integrável ao Application Insights por
`APPLICATIONINSIGHTS_CONNECTION_STRING` (FR-024). Amostragem tail-based conforme
coding-guidelines (100% de erros e requisições lentas; 1-5% do restante).

**Convenção de nomes (Q7)**: OpenTelemetry semantic conventions com namespace `stoke.*`.
Conjunto estável e pequeno de spans/métricas no beta:

| Nome | Tipo | Quando |
|------|------|--------|
| `stoke.session.create` | span | criação/pré-provisionamento de sessão |
| `stoke.session.get` | span | consulta de estado de sessão |
| `stoke.session.stop` | span | parada de sessão |
| `stoke.session.delete` | span | encerramento de sessão |
| `stoke.warmup.probe` | span | execução de um `WarmupProbe` (keepalive) |
| `stoke.warmup.refill` | span | reconciliação/reabastecimento do pool |
| `stoke.store.read` | span | leitura no store durável |
| `stoke.store.write` | span | gravação (create/upsert/delete) no store durável |

Atributos comuns: `stoke.agent_definition_id`, `stoke.agent_session_id`,
`stoke.session.state`, `stoke.store.provider`, `stoke.warmup.strategy`. Segredos nunca
são emitidos como atributos.

## Segurança

O security-review arquitetural (docs/features/stoke-beta/security-review-architecture.md,
veredito APPROVED_WITH_CONTROLS) consolidou os controles de design nos ADRs abaixo. Nenhum
achado Critical; os controles são preventivos e aplicados antes da implementação.

| Achado | Controle | Registrado em |
|--------|----------|---------------|
| SEC-001 | Sanitização de path (hash/allowlist + caminho canônico) no FileSystem provider | ADR 0001 |
| SEC-002 | Desserialização segura por schema + allowlist de `type` + limite de tamanho | ADR 0001 |
| SEC-006 | Ciclo read-check-etag-write sob o mesmo lock + timeout de aquisição | ADR 0001 |
| SEC-007 | Teto configurável de `targetSize` + backoff/jitter no scheduler | ADR 0003 |
| SEC-004 | Credencial determinística em produção (`AZURE_TOKEN_CREDENTIALS`/injeção) | ADR 0005 |
| SEC-005 | Precedência de fallback + segredos nunca persistidos no store | ADR 0005, 0006 (telemetria) |
| SEC-003, SEC-009 | Redação por allowlist; `agent_session_id` como sensível; partição não é authz | ADR 0006, data-model.md |
| SEC-008, SEC-010 | Modelo de confiança in-process + hardening leve; endpoint de probe validado | ADR 0007, contracts/warmup-probe.md |
| SEC-011 | Pinning/SBOM/assinatura de pacotes | Empacotamento/Release (tasks de CI no decompose) |

## Abordagem de Testes / Suíte de Conformidade

Suíte de conformidade compartilhada valida equivalência semântica entre Python e .NET
(SC-001, CC-001..CC-007). **Mecanismo (Q3, ADR 0004)**: fixtures de cenário agnósticas de
linguagem (YAML/JSON) como fonte única de verdade, executadas por um harness fino por
linguagem; foco em equivalência comportamental/semântica, não em snapshots de output.

Unit tests por linguagem para providers, concorrência otimista e validação de idle timeout.
O **cross-process file-lock** do provider FileSystem/JSON é testado com processos/handles
concorrentes reais (garante que o lock coordena escritores). O `VirtualClock` injetado
(ADR 0003) torna os testes de warm-up/idle determinísticos, sem espera real. Teste de
inspeção de dependências garante ausência de SDK de store no core (CC-004).

## Empacotamento / Release

PyPI `foundry-stoke` (import `foundry_stoke`) e NuGet `Foundry.Stoke`, Apache-2.0. Go
adiado (sem módulo publicado no beta). **Versionamento (Q4, ADR 0004)**: semver
**independente por linguagem** — PyPI e NuGet versionados de forma independente; um hotfix
de uma linguagem não força bump na outra. A equivalência é assegurada pela suíte de
conformidade, não por um número de versão compartilhado. Pipelines de publicação
independentes por linguagem, cada uma com sua tag/semver.

## Riscos

| Risco | Impacto | Mitigação |
|-------|---------|-----------|
| Sem SDK Foundry oficial para Go | Alto | **Go adiado** (ADR 0004); contrato projetado para admitir Go depois sem quebra |
| Session control típado não confirmado em .NET | Médio | REST fallback via `Azure.Core` atrás de `ISessionController` (ADR 0005); migra para típado sem quebra |
| Semântica de "atividade" que reseta idle timer não documentada | Médio | Probe reusado para renovar pool; validar empiricamente (NEEDS RESEARCH em research.md) |
| Enum de status de sessão não documentado | Baixo | Enum próprio + tradução em runtime (ADR 0005) || Advisory file lock não porta para NFS/SMB | Baixo | Provider FileSystem é para dev local; documentado (ADR 0001) |
| Scheduler bloquear thread por engano | Médio | Hard constraint não bloqueante + clock injetável com delay async (ADR 0003) |

## Comandos

Monorepo com implementações isoladas; os comandos são por linguagem. A estrutura de
projeto ainda será criada no decompose/implement; os comandos abaixo refletem o stack
decidido (ruff/pytest no Python; dotnet CLI no .NET) e serão ajustados quando os projetos
forem scaffoldados.

### Build
```
# Python (nada a compilar; validação de tipos)
cd python && python -m pip install -e ".[dev]" && pyright
# .NET
cd dotnet && dotnet build --configuration Release
```

### Testes
```
# Python
cd python && pytest -q
# .NET
cd dotnet && dotnet test --verbosity normal
```

### Lint/Formatação
```
# Python
cd python && ruff check . && ruff format --check .
# .NET
cd dotnet && dotnet format --verify-no-changes
```

### Execução Local
```
# Biblioteca (SDK): sem entrypoint executável. Exercitar via testes/exemplos:
# Python
cd python && pytest -q tests/conformance
# .NET
cd dotnet && dotnet test --filter Category=Conformance
```

## Engineering Practices

| Prática | Decisão | Referência |
|---------|---------|------------|
| Isolamento por linguagem | Monorepo com `python/` e `dotnet/` isolados (sem compartilhar build/fonte); Go reservado/adiado | ADR 0004; coding-guidelines |
| Versionamento/Release | semver **independente** por linguagem (PyPI e NuGet), pipelines separados | ADR 0004 |
| Conformidade cross-language | Fixtures agnósticas (YAML/JSON) executadas por harness fino por linguagem | ADR 0004 |
| Observabilidade | OpenTelemetry, namespace `stoke.*`, tail-based sampling | plan.md (Observabilidade); coding-guidelines |
| Auth/segredos | `DefaultAzureCredential` + fallback; segredos via env/vault, nunca hardcoded | ADR 0005 |
| Concorrência | Schedulers de warm-up estritamente não bloqueantes (delay async; clock injetável) | ADR 0003 |

## ADRs de Referência

Este plano segue os ADRs (todos Proposed):

| ADR | Domínio | Cobre |
|-----|---------|-------|
| [0001](../../architecture/decisions/0001-durable-store-provider.md) | durable-store-provider | Provider agnóstico, modelo Cosmos-friendly sem acoplamento, file-lock |
| [0002](../../architecture/decisions/0002-control-plane-boundary.md) | control-plane-boundary | Fronteira control-plane-only (sem cliente data-plane) |
| [0003](../../architecture/decisions/0003-warmup-strategies-scheduler.md) | warmup-strategies-scheduler | Estratégias plugáveis + probe + scheduler não bloqueante com clock injetável |
| [0004](../../architecture/decisions/0004-cross-language-api-consistency.md) | cross-language-api-consistency | Conformidade por fixtures, semver independente, Go adiado |
| [0005](../../architecture/decisions/0005-authentication-strategy.md) | authentication-strategy | `DefaultAzureCredential` + fallback; REST fallback .NET via `ISessionController` |
| [0006](../../architecture/decisions/0006-secrets-telemetry-redaction.md) | secrets-telemetry-redaction | Redação por allowlist; segredos e handles nunca emitidos; `agent_session_id` sensível |
| [0007](../../architecture/decisions/0007-pluggable-provider-trust-model.md) | pluggable-provider-trust-model | Confiança in-process de providers/probes + hardening leve; endpoint de probe validado |
