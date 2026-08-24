# Especificação de Feature: Stoke Beta (SDK multilinguagem para agentes hospedados no Foundry)

- **Criado em**: 2026-08-21
- **Status**: Draft

## Sumário Executivo

- **Objetivo**: Entregar o Stoke, um SDK multilinguagem (Python e .NET no beta; Go adiado até existir um SDK oficial do Foundry para Go) que controla agentes hospedados no Foundry Agent Service, cobrindo cinco capacidades beta: store durável via provider desacoplado, aquecimento de sessões, controle de sessão/estado, integração com o Foundry no nível de control-plane de sessão (ciclo de vida) com probe de aquecimento plugável, e consistência de API entre linguagens.
- **Usuário primário**: Desenvolvedores de aplicações que operam agentes hospedados no Foundry e precisam gerenciar o ciclo de vida de sessões (status oficiais `AgentSessionStatus`, com a retomada de sessões ociosas refletida como observação derivada) sem escrever integração de baixo nível.
- **Valor entregue**: Reduz a latência percebida de reativação de agentes, padroniza o acesso a store durável sem acoplamento a um banco específico e oferece uma API idiomática e semanticamente equivalente nas linguagens do beta (Python e .NET), com o contrato projetado para admitir Go depois sem quebra.
- **Escopo**: Incluído: contrato de store durável (compatível com Cosmos por design, sem dependência do SDK do Cosmos), providers de referência InMemory e FileSystem/JSON, estratégias de aquecimento plugáveis, controle de sessão/estado, integração com o Foundry no nível de control-plane de sessão (criar/referenciar/retomar/consultar estado) com probe de aquecimento plugável (ping genérico Responses opcional embutido mais hook de probe fornecido pelo usuário para Invocations/containers customizados), autenticação Entra ID com fallback, observabilidade via OpenTelemetry. A Stoke NÃO embarca cliente de tráfego dual-protocolo; a aplicação usa o SDK oficial do Foundry para o data-plane. Excluído: ver Não-Escopo.
- **Tipo de mudança**: new surface
- **Descreve capacidade de IA**: não
- **Critério primário de sucesso**: A superfície pública das linguagens do beta (Python e .NET) é semanticamente equivalente e passa em uma suíte de conformidade compartilhada, com providers InMemory e FileSystem operando o ciclo completo de sessão sem qualquer dependência do SDK do Cosmos no core.

## Não-Escopo *(required)*

- **Orquestração multiagente**: coordenar múltiplos agentes em um fluxo único não faz parte do beta. Evita expandir o SDK para um motor de orquestração.
- **Roteamento e gestão de histórico de conversa**: além de referenciar identificadores de sessão/conversa, o SDK não gerencia histórico, roteamento ou memória conversacional.
- **Gestão de custo/billing**: nenhum controle de custo, cotas ou faturamento é oferecido pelo SDK.
- **Interface gráfica (UI)**: o beta é biblioteca/SDK, sem qualquer componente de UI.
- **Políticas avançadas de retry/circuit-breaker**: não há política avançada de resiliência no beta além de tratamento de erro básico e propagação.
- **Cache de respostas**: o SDK não faz cache de respostas de agente.
- **Providers de store baseados em Azure**: Cosmos, Table e Redis não são embarcados nem dependências do beta. São providers externos/comunitários posteriores.
- **Cliente de tráfego dual-protocolo (Responses/Invocations)**: a Stoke não implementa um cliente de tráfego de propósito geral para payloads de aplicação. A aplicação usa o SDK oficial do Foundry para o data-plane; a Stoke só toca o control-plane de sessão e um probe mínimo de warm-up.

## Assumptions

- Os SDKs oficiais do Foundry expõem as operações necessárias para gerenciar sessões, protocolos Responses/Invocations e o status oficial de sessão `AgentSessionStatus`. O Stoke não inventa APIs; apenas encapsula as oficiais.
- O idle timeout do compute de sessão é configurável entre 5 e 60 minutos, com padrão de 15 minutos, conforme o produto controlado.
- `$HOME` e `/files` são persistidos pela plataforma entre reativações de sessão; o Stoke não replica essa persistência.
- Variáveis de ambiente padrão do Foundry (`FOUNDRY_PROJECT_ENDPOINT`, `AZURE_AI_MODEL_DEPLOYMENT_NAME`, `APPLICATIONINSIGHTS_CONNECTION_STRING`) estão disponíveis no ambiente de execução.
- Autenticação primária via `DefaultAzureCredential` (Entra ID) está disponível no ambiente; fallback por API key/connection string é usado apenas quando a credencial primária não estiver disponível.
- Versões mínimas suportadas no beta: Python 3.10+ e .NET 8. Go está adiado: a plataforma expõe apenas a REST `/sessions` para Go, sem SDK oficial do Foundry; o contrato cross-language é projetado para admitir Go depois sem quebra.
- O monorepo hospeda `python/` e `dotnet/` no beta; `go/` fica reservado/adiado até existir SDK oficial. Publicação em PyPI (`foundry-stoke`, import `foundry_stoke`) e NuGet (`Foundry.Stoke`), sob licença Apache-2.0, com versionamento semver independente por linguagem.

## User Scenarios & Tests *(required)*

### User Story 1 - Controlar ciclo de vida de sessão de agente (Priority: P1)

Um desenvolvedor usa o Stoke para abrir uma sessão em um agente hospedado, enviar requisições, observar as transições de status oficiais (incluindo o efeito de retomada de uma sessão ociosa referenciada de novo) e encerrar a sessão de forma idiomática à sua linguagem.

O modelo de status é o enum oficial `AgentSessionStatus` do Foundry, com oito valores (strings minúsculas): `creating`, `active`, `idle`, `updating`, `failed`, `deleting`, `deleted`, `expired`. O ciclo de vida real é `creating` -> `active` <-> `idle` -> (`updating` | `failed` | `deleting` | `deleted` | `expired`). "Retomada" (resumed) NÃO é um status oficial: é a transição derivada `idle` -> `active` observada quando uma sessão ociosa é referenciada de novo, refletida pela Stoke via um marcador derivado (`resumed_at`), nunca como um status armazenado. Qualquer valor de status desconhecido ou futuro MUST ser exposto como `UNKNOWN`, nunca silenciosamente convertido em outro status.

**Why this priority**: É a capacidade central do SDK. Sem o controle de sessão/estado, nenhuma outra capacidade tem valor.

**Independent Test**: Pode ser testada de ponta a ponta abrindo uma sessão, referenciando-a pela API de ciclo de vida, deixando a sessão entrar em `idle` e referenciando-a de novo (o efeito de retomada, observado como `active` outra vez), verificando a persistência de `$HOME`/`/files` pela plataforma.

**Acceptance Scenarios**:

1. **Given** um agente hospedado configurado, **When** o desenvolvedor abre uma sessão, **Then** o SDK retorna um `agent_session_id` e a sessão evolui para o status `active`.
2. **Given** uma sessão `active` sem atividade além do idle timeout, **When** o timeout é atingido, **Then** o SDK reflete o status `idle` e, ao referenciar a sessão de novo, ela é observada como `active` outra vez (efeito de retomada, marcado por `resumed_at`) preservando `$HOME`/`/files`.
3. **Given** uma sessão em qualquer status, **When** o desenvolvedor encerra a sessão, **Then** os recursos associados são liberados e novas operações sobre a sessão encerrada retornam erro determinístico.
4. **Given** uma sessão cujo status oficial não é reconhecido pela versão corrente da Stoke, **When** o estado é consultado, **Then** o SDK expõe `UNKNOWN` sem coagir para `active` nem mascarar o valor.

---

### User Story 2 - Persistir e recuperar estado via store durável desacoplado (Priority: P1)

Um desenvolvedor persiste registros de estado (id estável, partition key, JSON, controle de concorrência otimista) usando um provider de store durável, sem escolher ainda um banco de produção.

**Why this priority**: O contrato de store durável é a fundação para providers externos (incluindo um Cosmos de terceiros) e habilita persistência sem acoplamento.

**Independent Test**: Pode ser testada implementando o ciclo CRUD + query-por-partição contra os providers InMemory e FileSystem/JSON, validando serialização JSON e concorrência otimista via etag/versão.

**Acceptance Scenarios**:

1. **Given** um provider InMemory, **When** o desenvolvedor grava um registro com id e partition key, **Then** o registro é recuperável por id e por consulta de partição.
2. **Given** um registro existente com etag conhecido, **When** duas gravações concorrentes são tentadas com o mesmo etag, **Then** apenas a primeira sucede e a segunda falha por conflito de concorrência otimista.
3. **Given** um provider FileSystem/JSON, **When** o processo é reiniciado, **Then** os registros previamente gravados permanecem recuperáveis.

---

### User Story 3 - Manter agentes aquecidos por estratégia plugável (Priority: P2)

Um desenvolvedor seleciona uma estratégia de aquecimento (pings/resumption agendados ou pool pré-provisionado de sessões prontas) para reduzir a latência de reativação.

**Why this priority**: Reduz latência percebida em cenários de uso intermitente, mas depende do controle de sessão (P1) já existir.

**Independent Test**: Pode ser testada configurando cada estratégia de forma independente e observando que sessões permanecem/tornam-se prontas antes do idle timeout, ou que um pool de N sessões por definição de agente é mantido no tamanho-alvo.

**Acceptance Scenarios**:

1. **Given** a estratégia de pings/resumption agendados, **When** uma sessão se aproxima do idle timeout, **Then** o SDK executa a reativação para mantê-la utilizável.
2. **Given** a estratégia de pool com tamanho-alvo N por definição de agente, **When** uma sessão do pool é consumida, **Then** o pool é reabastecido até o tamanho-alvo.
3. **Given** múltiplas definições de agente, **When** cada uma define seu próprio N, **Then** os pools são dimensionados independentemente por definição.

---

### User Story 4 - Integrar com o Foundry no control-plane de sessão com aquecimento plugável e autenticação segura (Priority: P2)

Um desenvolvedor integra a aplicação ao Foundry no nível de ciclo de vida de sessão (criar, referenciar, retomar e consultar estado), mantém sessões aquecidas por um probe plugável (ping genérico embutido sobre Responses ou probe fornecido pelo usuário para containers Invocations/customizados) e autentica via Entra ID (`DefaultAzureCredential`) com fallback por API key/connection string. O tráfego real de conversa/negócio continua sendo enviado pela aplicação via SDK oficial do Foundry.

**Why this priority**: Habilita o controle real do ciclo de vida da sessão e a manutenção de agentes aquecidos; o tráfego de dados fica a cargo do SDK oficial, evitando duplicar os protocolos.

**Independent Test**: Pode ser testada criando e retomando uma sessão pela API de ciclo de vida; exercitando o keepalive pelo probe genérico embutido e por um probe fornecido pelo usuário; e verificando a autenticação primária e o caminho de fallback de forma isolada.

**Acceptance Scenarios**:

1. **Given** uma definição de agente configurada, **When** o desenvolvedor cria e retoma uma sessão pela API de ciclo de vida, **Then** o SDK retorna/reutiliza o `agent_session_id` e reflete o estado da sessão de forma agnóstica de protocolo.
2. **Given** um agente compatível com Responses, **When** o keepalive usa o probe genérico embutido dentro da janela de idle, **Then** a sessão permanece utilizável (não entra em Idle) sem que a Stoke envie tráfego de aplicação.
3. **Given** um agente Invocations/container customizado, **When** o desenvolvedor fornece um probe (callback/delegate) e o keepalive o executa, **Then** a sessão permanece utilizável usando exclusivamente o probe fornecido pelo usuário.
4. **Given** credenciais Entra ID disponíveis, **When** uma operação de control-plane é executada, **Then** o SDK autentica via `DefaultAzureCredential`; **When** indisponíveis e há fallback configurado, **Then** usa API key/connection string; **When** nenhuma credencial disponível, **Then** falha com erro claro indicando ausência de credenciais.

---

### User Story 5 - Usar API equivalente entre linguagens (Priority: P3)

Um desenvolvedor que conhece o Stoke em uma linguagem consegue reconhecer e usar os mesmos conceitos e operações em outra linguagem, respeitando a idiomática de cada uma.

**Why this priority**: Aumenta a adoção e reduz custo de manutenção, mas é uma propriedade transversal validada após as capacidades funcionais.

**Independent Test**: Pode ser testada por uma suíte de conformidade compartilhada que descreve cenários independentes de linguagem e é executada nas três implementações.

**Acceptance Scenarios**:

1. **Given** um cenário descrito na suíte de conformidade, **When** ele é executado em Python, .NET e Go, **Then** todas as três implementações produzem resultados semanticamente equivalentes.
2. **Given** uma operação assíncrona, **When** executada em Python (async, com sync onde fizer sentido), .NET (Task-based) e Go (`context.Context`), **Then** a semântica pública é equivalente apesar das diferenças idiomáticas.

### Edge Cases

- O que acontece quando uma sessão é reativada após o compute ter sido reciclado, mas `$HOME`/`/files` permanecem persistidos pela plataforma?
- Como o SDK se comporta quando o idle timeout configurado está fora do intervalo suportado (5-60 min)?
- O que acontece quando o provider FileSystem/JSON encontra um arquivo corrompido ou parcialmente escrito?
- Como o pool de aquecimento reage quando o tamanho-alvo não pode ser atingido por indisponibilidade do serviço?
- O que acontece quando o probe genérico de aquecimento sobre Responses é aplicado a um agente cujo container só aceita Invocations? (requer um probe fornecido pelo usuário)

### Failure Modes *(include if the feature has external dependencies or shared state)*

- O que acontece quando o Foundry Agent Service está indisponível ou excede o tempo limite durante abertura/reativação de sessão? O SDK propaga um erro tipado sem retry avançado no beta.
- O que acontece quando duas gravações concorrentes ocorrem sobre o mesmo registro no store durável? Vence a primeira; a segunda falha por conflito de concorrência otimista (etag/versão).
- O que acontece quando a estratégia de aquecimento por pings falha em reativar uma sessão antes do idle timeout? A sessão entra em Idle e é reativada sob demanda na próxima requisição.
- Qual modelo de consistência é exigido? O store durável opera com consistência do provider subjacente; o contrato exige apenas concorrência otimista por registro (leitura da própria escrita não é garantida entre providers).
- O que acontece quando as credenciais Entra ID expiram durante uma sessão longa? O SDK deve renovar via `DefaultAzureCredential`; se falhar e houver fallback configurado, usa o fallback; caso contrário, retorna erro de autenticação.

## Requirements *(required)*

### Functional Requirements

Controle de sessão/estado:

- **FR-001**: O SDK MUST permitir abrir uma sessão em um agente hospedado e retornar um identificador estável de sessão (`agent_session_id`).
- **FR-002**: O SDK MUST expor o status de sessão como o enum oficial `AgentSessionStatus` (`creating`, `active`, `idle`, `updating`, `failed`, `deleting`, `deleted`, `expired`) mais um valor `UNKNOWN` de fallback, refletindo as transições entre eles. Valores de status desconhecidos ou futuros MUST ser mapeados para `UNKNOWN`, nunca silenciosamente coagidos para outro status.
- **FR-003**: O SDK MUST refletir a retomada de uma sessão `idle` (transição derivada `idle` -> `active` ao referenciá-la de novo) via um marcador derivado (`resumed_at`), preservando `$HOME` e `/files` persistidos pela plataforma. "Retomada" não é um status armazenado.
- **FR-004**: O SDK MUST permitir configurar o idle timeout dentro do intervalo de 5 a 60 minutos, com padrão de 15 minutos.
- **FR-005**: O SDK MUST permitir encerrar explicitamente uma sessão e retornar erro determinístico em operações subsequentes sobre a sessão encerrada.

Store durável (contrato desacoplado, compatível com Cosmos por design):

- **FR-006**: O SDK MUST definir uma interface pública de provider de store durável, agnóstica de tecnologia, cujo core NÃO contenha código específico de Cosmos nem dependência do SDK do Cosmos.
- **FR-007**: O modelo de dados do store durável MUST exigir, por registro, um `id` estável e uma `partition key`, serialização para JSON e suporte a concorrência otimista (etag/versão).
- **FR-008**: A interface de store durável MUST oferecer semântica mínima de CRUD mais consulta-por-partição.
- **FR-009**: A interface MUST ser projetada para que um provider Cosmos de terceiros possa implementá-la sem alterações no core do Stoke.
- **FR-010**: O beta MUST incluir dois providers de referência: InMemory (testes/dev) e FileSystem/JSON (dev local).
- **FR-011**: O beta MUST NOT embarcar ou depender de providers Cosmos, Table ou Redis.

Aquecimento (estratégias plugáveis):

- **FR-012**: O SDK MUST expor uma interface comum de estratégia de aquecimento, selecionável pelo usuário.
- **FR-013**: O SDK MUST prover a estratégia de pings/resumption agendados para manter ou reativar sessões antes do idle timeout.
- **FR-014**: O SDK MUST prover a estratégia de pré-provisionamento de um pool de sessões prontas.
- **FR-015**: A estratégia de pool MUST suportar múltiplas definições de agente, com N sessões quentes por agente e tamanho-alvo dimensionável por agente.

Integração com o Foundry (control-plane de sessão):

- **FR-016**: O SDK MUST integrar-se ao Foundry no nível de ciclo de vida de sessão (control-plane): criar, referenciar, retomar (resume) e consultar o estado de uma sessão, de forma agnóstica de protocolo.
- **FR-017**: O SDK MUST prover uma abstração de probe de aquecimento (warm-up): o pré-provisionamento usa exclusivamente o ciclo de vida de sessão (sem protocolo de dados) e o keepalive usa um probe plugável, com um ping genérico opcional embutido sobre Responses (compatível com OpenAI) e um hook de probe fornecido pelo usuário para agentes Invocations/containers customizados.
- **FR-017a**: O SDK MUST NOT embarcar um cliente de tráfego de propósito geral para Responses/Invocations de payloads de aplicação; o tráfego de dados (conversa/negócio) é responsabilidade do SDK oficial do Foundry na aplicação.
- **FR-018**: O SDK MUST usar os SDKs oficiais do Foundry, sem inventar APIs.
- **FR-019**: O SDK MUST autenticar primariamente via Entra ID usando `DefaultAzureCredential`.
- **FR-020**: O SDK MUST oferecer um fallback de autenticação por API key/connection string quando a credencial primária não estiver disponível.

Consistência entre linguagens e API:

- **FR-021**: O SDK MUST expor uma API assíncrona idiomática por linguagem: Python async (com sync onde fizer sentido), .NET baseada em Task, Go com `context.Context`.
- **FR-022**: A superfície pública MUST ser semanticamente equivalente entre Python, .NET e Go.
- **FR-023**: O SDK MUST manter uma pegada leve (lightweight footprint), evitando dependências desnecessárias no core.

Observabilidade:

- **FR-024**: O SDK MUST emitir telemetria via OpenTelemetry, integrável ao Application Insights por meio de `APPLICATIONINSIGHTS_CONNECTION_STRING`.

### Key Entities *(include if the feature involves data)*

- **Sessão de agente**: representa uma sessão em um agente hospedado; atributos chave incluem `agent_session_id`, status oficial `AgentSessionStatus` (oito valores mais `UNKNOWN` de fallback), o marcador derivado `resumed_at` (transição `idle` -> `active` observada) e configuração de idle timeout.
- **Registro de store durável**: unidade persistida no store; atributos chave incluem `id` estável, `partition key`, payload JSON e token de concorrência (etag/versão).
- **Provider de store durável**: implementação da interface de persistência; no beta há InMemory e FileSystem/JSON; providers externos (ex.: Cosmos) implementam a mesma interface.
- **Estratégia de aquecimento**: implementação da interface comum de warm-up; variantes por pings/resumption agendados e por pool pré-provisionado.
- **Definição de agente**: identifica um agente hospedado para fins de dimensionamento de pool (N sessões quentes, tamanho-alvo).

## Success Criteria *(required)*

### Measurable Outcomes

- **SC-001**: A suíte de conformidade compartilhada passa nas três linguagens (Python, .NET, Go) com resultados semanticamente equivalentes em 100% dos cenários definidos.
- **SC-002**: O core do Stoke não possui nenhuma referência ou dependência ao SDK do Cosmos, verificável por inspeção de dependências, e um provider externo consegue implementar a interface sem alterar o core.
- **SC-003**: Os providers InMemory e FileSystem/JSON completam o ciclo CRUD + consulta-por-partição, incluindo pelo menos um caso de conflito de concorrência otimista resolvido corretamente.
- **SC-004**: Ambas as estratégias de aquecimento são configuráveis e demonstram, em teste, manter/reabastecer sessões antes do idle timeout ou até o tamanho-alvo por definição de agente.
- **SC-005**: A integração de control-plane de sessão (criar/referenciar/retomar/consultar) completa de ponta a ponta com autenticação primária Entra ID e com o fallback configurado; o keepalive funciona tanto pelo probe genérico embutido (Responses) quanto por um probe fornecido pelo usuário (Invocations/container customizado).

## Conformance Criteria *(required)*

### Conformance Cases

| ID | Cenário | Entrada | Saída Esperada |
|----|---------|---------|----------------|
| CC-001 | Ciclo de sessão feliz | Abrir sessão, referenciar pela API de ciclo de vida, aguardar `idle`, referenciar de novo | `agent_session_id` retornado; transições `creating` -> `active` -> `idle` -> `active` (retomada derivada, `resumed_at` marcado); `$HOME`/`/files` preservados |
| CC-002 | Idle timeout inválido | Configurar idle timeout = 120 min | Erro de validação indicando intervalo suportado 5-60 min |
| CC-008 | Status desconhecido/futuro | Consultar sessão cujo status oficial não é reconhecido pela versão corrente | Status exposto como `UNKNOWN`; nunca coagido para `active` nem mascarado |
| CC-003 | Concorrência otimista no store | Duas gravações no mesmo registro com o mesmo etag | Primeira sucede; segunda falha por conflito de concorrência |
| CC-004 | Core não deve acoplar Cosmos | Inspeção de dependências do core do Stoke | Must NOT conter dependência ou import do SDK do Cosmos |
| CC-005 | Fallback de autenticação | Entra ID indisponível, API key configurada, operação de ciclo de vida de sessão | Autentica pelo fallback e completa a operação |
| CC-006 | Pool por definição de agente | Duas definições de agente com N distintos consumindo sessões | Cada pool é reabastecido até seu próprio tamanho-alvo, de forma independente |
| CC-007 | Keepalive por probe fornecido pelo usuário | Agente Invocations/container customizado com probe (callback/delegate) fornecido, keepalive dentro da janela de idle | Sessão permanece utilizável usando exclusivamente o probe do usuário; a Stoke não envia tráfego de aplicação |

## Invariants

- Cada registro do store durável é identificado unicamente pela combinação de `id` e `partition key`.
- Uma gravação com etag/versão desatualizado nunca sobrescreve uma versão mais recente (concorrência otimista sempre honrada).
- O core do Stoke nunca importa ou depende do SDK do Cosmos (ou de qualquer SDK de store de produção) em nenhum caminho de código.
- A superfície pública permanece semanticamente equivalente entre as três linguagens dentro de uma mesma versão do Stoke.
- Uma sessão encerrada nunca aceita novas operações sem erro determinístico.
- `$HOME` e `/files` nunca são replicados ou sobrescritos pelo Stoke; sua persistência é responsabilidade da plataforma.

## Compatibility and Transition *(required when Change type is not "new surface")*

N/A: superfície nova e puramente aditiva (primeiro release beta do Stoke).

## Related Specs

- Nenhuma spec relacionada no momento (primeiro beta).

## Spec Evolution Log *(required)*

| Versão | Data | Resumo da Mudança | Gatilho | Autor |
|--------|------|-------------------|---------|-------|
| 1.0 | 2026-08-21 | Rascunho inicial do beta do Stoke (5 capacidades) | new work | deividfoggi |
| 1.1 | 2026-08-21 | Amendment: integração com o Foundry restrita ao control-plane de sessão (criar/referenciar/retomar/consultar) com probe de aquecimento plugável; remoção do cliente de tráfego dual-protocolo Responses/Invocations do escopo | spec-drift (boundary shift) | deividfoggi |
| 1.2 | 2026-08-21 | Estreitamento de escopo: linguagens do beta = Python + .NET apenas. Go adiado até existir um SDK oficial do Foundry para Go (a pesquisa confirmou que Go possui somente a REST `/sessions`, sem SDK oficial). O contrato cross-language permanece projetado para admitir Go depois sem quebra. Referências a "três linguagens" em User Stories/FR/SC (US5, FR-021, FR-022, SC-001) devem ser lidas, no beta, como Python + .NET; Go permanece diferido. | scope narrowing (no official Go SDK) | deividfoggi |
| 1.3 | 2026-08-24 | Reconciliação do modelo de status de sessão com o contrato oficial confirmado. O status de sessão é o enum oficial `AgentSessionStatus` (`creating`, `active`, `idle`, `updating`, `failed`, `deleting`, `deleted`, `expired`) mais um `UNKNOWN` de fallback. "Resumed" deixa de ser tratado como status de primeira classe: é a transição derivada `idle` -> `active` refletida via `resumed_at`. Valores desconhecidos/futuros são expostos como `UNKNOWN`, nunca coagidos. Atualizados US1, FR-002/FR-003, Key Entities, CC-001, e adicionado CC-008. Fonte oficial: https://learn.microsoft.com/en-us/javascript/api/@azure/ai-projects/agentsessionstatus | spec-drift (state-model reconciliation vs official contract) | deividfoggi |

## Reasoning Log

- **2026-08-21 — Escopo de integração com o Foundry restrito ao control-plane de sessão.**
  - **Decisão**: A Stoke atua como control-plane do ciclo de vida de instâncias de agente hospedadas (warm-up, controle de sessão, estado, store durável). Ela NÃO atua como cliente data-plane que encapsula os protocolos Responses/Invocations para tráfego de aplicação.
  - **Gatilho**: spec-drift (boundary shift) detectado durante a implementação; decisão aprovada pelo usuário.
  - **Racional**: Um cliente dual-protocolo duplicaria o SDK oficial do Foundry, adicionaria acoplamento e peso, e não funcionaria genericamente para Invocations (schema do payload definido pelo container do usuário). A aplicação continua usando o SDK oficial para o tráfego real de conversa/negócio.
  - **Fundamentação (docs oficiais)**: o idle timeout é medido "após a requisição mais recente"; "o compute segue a sessão"; sessões são criadas/provisionadas no primeiro uso. Logo, o pré-provisionamento (pool pronto) é alcançável apenas via criar/referenciar/retomar sessão (control-plane), sem protocolo de dados. O keepalive precisa de atividade dentro da janela de idle, provida por um probe mínimo e plugável: ping genérico opcional sobre Responses (compatível com OpenAI) e, para Invocations/containers customizados, um probe fornecido pelo usuário.
  - **Artefatos tocados**: Sumário Executivo (Objetivo, Escopo), Não-Escopo, User Story 4 e cenários, Edge Cases, FR-016/FR-017/FR-017a, SC-005, CC-001/CC-005, CC-007 (novo).
  - **Impacto**: High (mudança de fronteira de story). Re-decomposição de tasks necessária antes de retomar a implementação.
