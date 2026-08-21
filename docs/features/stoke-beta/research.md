# Research: Stoke Beta

- **Criado em**: 2026-08-21
- **Status**: Finalizado (decisões fechadas; gaps/NEEDS RESEARCH registrados como assunções)
- **Spec base**: docs/features/stoke-beta/spec.md (v1.2)

Este documento consolida o mapeamento das superfícies oficiais do Foundry por linguagem
para o control-plane de sessão, o que a plataforma provê nativamente e o que a Stoke
precisa implementar. Onde a operação/tipo oficial não pôde ser confirmada na
documentação, está marcado como **NEEDS RESEARCH** e vira uma decisão explícita, nunca
uma API inventada (FR-018).

## Fontes oficiais consultadas

| Fonte | Uso |
|-------|-----|
| learn.microsoft.com/azure/foundry/agents/how-to/manage-hosted-sessions | API de sessão (control-plane), pivots rest/python/azd |
| learn.microsoft.com/azure/foundry/agents/concepts/hosted-agents | Ciclo Active/Idle/Resumed, idle timeout, isolamento, observabilidade |
| learn.microsoft.com/agent-framework/hosting/foundry-hosted-agent | Hosting SDK (Python/C#/Go), auth, variáveis de ambiente |

## Fatos confirmados na plataforma (independentes de linguagem)

- **Ciclo de compute da sessão**: `Active` (compute rodando) -> `Idle` (sem requisição
  além do idle timeout; plataforma desprovisiona compute e persiste `$HOME`/`/files`) ->
  `Resumed` (mesma `agent_session_id` referenciada de novo; plataforma provisiona novo
  compute e restaura o estado).
- **`Resumed` NÃO é uma operação explícita**: é o efeito de referenciar novamente uma
  sessão ociosa. Não há endpoint "resume"; retomar acontece ao invocar/referenciar.
- **Idle timeout**: configurável por versão de agente, 5-60 min (300-3600 s), padrão 15
  min (900 s). Definido na criação da versão via `session_configuration.idle_timeout_seconds`.
  Não é alterável em uma versão existente (versões são imutáveis; cria-se nova versão).
- **Atividade que reseta o timer**: "cada requisição reseta o idle timer" e o timeout é
  medido "após a requisição mais recente". A doc trata invocações de dados
  (Responses/Invocations) como requisições. **NEEDS RESEARCH**: não está documentado se
  uma chamada de control-plane (`GET /sessions/{id}`) conta como atividade que reseta o
  timer. Assunção de projeto: **não conta**; por isso o keepalive exige um probe (ver
  abaixo), consistente com o amendment da spec.
- **Pré-provisionamento**: sessões são criadas no primeiro uso, mas há criação explícita
  (`POST .../endpoint/sessions`) para pré-alocar antes da primeira invocação. Isso
  habilita o pool de warm-up **sem tráfego de dados**.
- **Stop x Delete**: `:stop` termina o compute preservando o volume persistente (pode ser
  retomada depois); `DELETE` libera os recursos. Stop de sessão já parada sucede sem erro.
- **Isolation key**: escopo por chamador. `Entra` (padrão) deriva do token; `Header` lê
  `x-ms-user-isolation-key`. É particionamento, não autorização.
- **Sem warm pool nativo**: a doc afirma explicitamente "no warm pool to size" e "no
  replica count". Confirma o valor da Stoke: o warm-up (pool/keepalive) é responsabilidade
  do control-plane que a Stoke implementa, não da plataforma.
- **Persistência**: `$HOME` e `/files` são persistidos pela plataforma entre idle/resume;
  sessão apagada após 30 dias de inatividade. A Stoke não replica isso (invariante da spec).
- **Observabilidade**: a plataforma injeta `APPLICATIONINSIGHTS_CONNECTION_STRING`; libs de
  protocolo emitem OpenTelemetry por padrão. Alinha com FR-024.
- **Auth**: `DefaultAzureCredential` é o caminho primário nos exemplos oficiais (Python e
  .NET). Alinha com FR-019.

## Mapeamento por linguagem (control-plane de sessão)

### Python — CONFIRMADO

Pacote: `azure-ai-projects>=2.3.0` + `azure-identity`.

| Operação Stoke | Tipo/método oficial | Confirmação |
|----------------|---------------------|-------------|
| Cliente | `AIProjectClient(endpoint, credential=DefaultAzureCredential())` | Confirmado |
| Configurar idle timeout | `SessionConfiguration(idle_timeout_seconds=...)` na `HostedAgentDefinition` (criação de versão) | Confirmado |
| Criar sessão (pré-provisionar) | `project.agents.create_session(agent_name, version_indicator=VersionRefIndicator(...))` -> `.agent_session_id`, `.status` | Confirmado |
| Consultar estado | `project.agents.get_session(agent_name, session_id)` -> `.agent_session_id`, `.status` | Confirmado |
| Listar sessões | `project.agents.list_sessions(agent_name)` | Confirmado |
| Parar sessão | `project.agents.stop_session(agent_name, session_id)` | Confirmado |
| Encerrar sessão | `project.agents.delete_session(agent_name, session_id)` | Confirmado |
| Probe genérico (Responses) | `project.get_openai_client(agent_name).responses.create(input=..., extra_body={"agent_session_id": id})` | Confirmado |

Notas:
- Valores concretos do enum de `status` (strings exatas para Active/Idle/Resumed) **não
  estão documentados**. NEEDS RESEARCH: inspecionar em runtime/SDK e mapear para o enum
  público da Stoke. A Stoke expõe um enum próprio e traduz.
- O SDK Python **não** provê cliente tipado de Invocations (a doc instrui usar `requests`).
  Isso reforça que o probe de Invocations é responsabilidade do usuário (FR-017).

### .NET — PARCIALMENTE CONFIRMADO

Pacotes: `Azure.AI.Projects` (prerelease) + `Azure.Identity`; hosting em
`Microsoft.Agents.AI.Foundry.Hosting` (prerelease).

| Operação Stoke | Situação |
|----------------|----------|
| Cliente + auth | `new AIProjectClient(endpoint, new DefaultAzureCredential())` | Confirmado |
| Session control tipado (create/get/list/stop/delete) | **NÃO confirmado**: a how-to de sessões não tem pivot C#. NEEDS RESEARCH |

Assunção ratificada (ADR 0005): onde o SDK .NET não expuser operações de
sessão tipadas, a Stoke chama a **API REST `/sessions` documentada** através do pipeline
HTTP do `Azure.Core`/`AIProjectClient` (mesma auth, mesmo endpoint), encapsulando-a atrás
da interface `SessionController`. Isso não é "inventar API": é consumir o contrato REST
oficial documentado. Migra para tipado, sem quebra, quando/se o SDK expuser as operações
(gap registrado abaixo).

### Go — SEM SDK OFICIAL (ADIADO no beta)

- A doc de hosting declara: **"Go support for Foundry hosted agents is coming soon"**.
- A doc de conceitos lista suporte de linguagem a **Python e C#** (runtime de container).
- Não há SDK Go oficial confirmado para control-plane de sessão nem `azure-identity` Go
  específico do Foundry (embora `azidentity` do Azure SDK for Go exista e forneça
  `DefaultAzureCredential`).
- A coding-guidelines do repo condiciona Go: "only include if an official Foundry Go SDK
  exists."

**Decisão (ADR 0004, spec v1.2)**: Go é **adiado**. O beta implementa **Python + .NET**
apenas. Implementar Go hoje exigiria um cliente REST próprio contra `/sessions` (não há
SDK oficial), o que conflita com FR-018 (não inventar/duplicar superfície sem base oficial
tipada) e adiantaria peso de manutenção sem SDK estável. O contrato cross-language
(interfaces agnósticas + fixtures de conformidade) permanece projetado para admitir Go
depois **sem quebra**: adicionar Go = novo harness sobre as mesmas fixtures + implementação
das interfaces já definidas.

## Superfície de dados (data-plane) — fora de escopo por design

Confirmado pela doc e pelo amendment da spec: o tráfego real (Responses/Invocations de
payloads de aplicação) é responsabilidade do SDK oficial do Foundry na aplicação. A Stoke
só toca:
1. control-plane de sessão (`/sessions`), e
2. um probe mínimo de keepalive (ping genérico Responses embutido, opcional; ou probe do
   usuário para Invocations/containers customizados).

A Stoke NÃO embarca cliente dual-protocolo (FR-017a).

## Implicações de design derivadas da pesquisa

- **Pré-provisionamento (pool)** = `create_session` em lote até o tamanho-alvo N por
  definição de agente; reabastecimento ao consumir. Não requer probe de dados.
- **Keepalive** = executar um probe dentro da janela de idle. Probe embutido = um
  `responses.create` mínimo (conta como atividade). Probe do usuário = callback/delegate
  para Invocations/containers customizados. A abstração `WarmupProbe` isola isso.
- **Manter o pool "quente"** também exige atividade periódica se `create_session` sozinho
  não resetar o timer. NEEDS RESEARCH (mesma incerteza de atividade acima). Mitigação de
  projeto: o scheduler do pool reusa o mesmo `WarmupProbe` para renovar sessões do pool.
- **Store durável** é independente do Foundry SDK — modelo compatível com Cosmos (id +
  partition key + etag/versão + payload JSON), providers InMemory e FileSystem/JSON no
  beta, sem dependência de SDK de store de produção (FR-006, FR-011).

## Decisões fechadas

As decisões antes abertas foram resolvidas e ancoradas em ADRs. Gaps sem confirmação
oficial permanecem registrados como assunções (NEEDS RESEARCH), nunca como API inventada.

| # | Tema | Decisão | Âncora |
|---|------|---------|--------|
| Q1 | Go (sem SDK oficial) | **Adiado**. Beta = Python + .NET. Contrato projetado para admitir Go depois sem quebra. | ADR 0004; spec v1.2 |
| Q2 | .NET session control | REST fallback contra `/sessions` via pipeline `Azure.Core`/`AIProjectClient`, atrás de `ISessionController`; Python usa o típado do `azure-ai-projects`. | ADR 0005 |
| Q3 | Suíte de conformidade | Fixtures de cenário agnósticas (YAML/JSON) como fonte única, executadas por harness fino por linguagem; foco em equivalência comportamental, não snapshots. | ADR 0004 |
| Q4 | Versionamento/release | semver **independente** por linguagem (PyPI e NuGet). | ADR 0004 |
| Q5 | Scheduler do warm-pool | Idiomático por linguagem (asyncio task / `BackgroundService`+`PeriodicTimer`) + clock/scheduler injetável; **hard constraint não bloqueante** (delay async; sem `Thread.Sleep`). | ADR 0003 |
| Q6 | File-locking no FileSystem | Advisory file lock cross-process **além** da concorrência otimista por etag; portabilidade limitada (NFS/SMB) — provider de dev local. | ADR 0001 |
| Q7 | Telemetria | OpenTelemetry com namespace `stoke.*` (ex.: `stoke.session.create`, `stoke.warmup.probe`, `stoke.store.write`). | plan.md (Observabilidade) |

## Gaps conhecidos / NEEDS RESEARCH (assunções, não APIs inventadas)

- **Strings do enum de status de sessão** (Active/Idle/Resumed) não documentadas: a Stoke
  expõe enum próprio e traduz em runtime. Validar por inspeção de SDK/runtime.
- **Se `create_session`/`GET /sessions/{id}` resetam o idle timer** não está documentado.
  Assunção: não resetam; por isso o keepalive usa um probe mínimo e o scheduler do pool
  reusa o `WarmupProbe` para renovar sessões. Validar empiricamente antes do GA.
- **Operações de sessão tipadas no SDK .NET** não confirmadas: assumir REST fallback até
  confirmação; migrar para tipado sem quebra quando disponível.
