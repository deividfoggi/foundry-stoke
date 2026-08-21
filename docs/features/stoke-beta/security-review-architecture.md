# Security Review - Stoke Beta

- **Modo**: Arquitetural (design-time)
- **Data**: 2026-08-21
- **Revisor**: Copilot Security Agent
- **Spec base**: docs/features/stoke-beta/spec.md (v1.2)
- **ADRs avaliados**: 0001, 0002, 0003, 0004, 0005 (todos Proposed)

## Sumário Executivo

**Veredito**: APPROVED_WITH_CONTROLS

O design da Stoke tem uma superfície de ataque pequena e bem contida: é uma biblioteca de control-plane, sem cliente de data-plane, sem endpoints expostos e sem provisionamento de infraestrutura no beta. Nenhum achado Critical foi identificado. Há três achados High que são controles de design (sanitização de path, segurança de desserialização de estado persistido e redação de segredos/telemetria) que devem ser incorporados aos ADRs e virar tasks obrigatórias antes da implementação. Como não existe código ainda, todos os controles são preventivos e baratos de aplicar agora.

## Superfície de Ataque

| Componente | Exposição | Dados que cruzam a fronteira | Risco inicial |
|------------|-----------|------------------------------|---------------|
| Provider FileSystem/JSON | Local (disco do host) | JSON persistido (não confiável na releitura), `id`/`partitionKey` usados em caminhos | Alto |
| CredentialProvider (fallback) | Local (env/config/vault) | API key / connection string (segredos) | Alto |
| Telemetria `stoke.*` + App Insights | Externa (sink de telemetria) | Atributos de span, mensagens de erro, `agent_session_id`, endpoints | Médio |
| WarmupProbe (embutido + hook do usuário) | Externa (endpoint Foundry) | Chamada de control/data-plane mínima; código do usuário in-process | Médio |
| SessionController (control-plane `/sessions`) | Externa (Foundry) | `agent_session_id`, token Entra ID | Médio |
| DurableStoreProvider plugável (terceiros) | In-process (código de terceiros) | Estado da Stoke; roda com plena confiança | Médio |
| Provider InMemory | In-process | Estado volátil | Baixo |

## Fronteiras de Confiança

1. **Disco -> processo** (FileSystem/JSON): o JSON relido é entrada não confiável. Fronteira de tampering e desserialização.
2. **Config/env/vault -> processo** (segredos de fallback): segredos entram; nunca devem sair via log, telemetria, erro ou store durável.
3. **Processo -> sink de telemetria** (App Insights): saída onde pode ocorrer vazamento de segredos/handles sensíveis.
4. **Processo -> Foundry** (SessionController/probe): token Entra ID e `agent_session_id` cruzam para o serviço.
5. **Processo <-> código plugável** (provider de store de terceiros e probe do usuário): rodam in-process com confiança total; a plataforma não os isola.

## Análise STRIDE - Achados

| ID | Ameaça (STRIDE) | Componente | Severidade | Status |
|----|-----------------|------------|------------|--------|
| SEC-001 | Tampering / Info Disclosure | FileSystem/JSON provider | High | OPEN |
| SEC-002 | Tampering | FileSystem/JSON provider | High | OPEN |
| SEC-003 | Info Disclosure | Telemetria/logs/erros | High | OPEN |
| SEC-004 | Spoofing / Elevation | CredentialProvider (DefaultAzureCredential) | Medium | OPEN |
| SEC-005 | Info Disclosure | CredentialProvider (fallback) | Medium | OPEN |
| SEC-006 | Tampering / DoS | FileSystem/JSON file-lock | Medium | OPEN |
| SEC-007 | DoS | Scheduler de warm-pool | Medium | OPEN |
| SEC-008 | Tampering / Elevation | Provider plugável de terceiros | Medium | OPEN |
| SEC-009 | Info Disclosure | `agent_session_id` / modelo de partição | Medium | OPEN |
| SEC-010 | Info Disclosure / SSRF | WarmupProbe | Low | OPEN |
| SEC-011 | Tampering (supply-chain) | Empacotamento/dependências | Low | OPEN |

## Status de Consolidação nos ADRs (2026-08-21)

Decisões de design consolidadas nos ADRs (Proposed). As tasks de implementação pertencem ao
decompose e permanecem em aberto.

| Achado | Decisão registrada em | Status da decisão |
|--------|------------------------|-------------------|
| SEC-001 | ADR 0001 (Notas de segurança) | RECORDED |
| SEC-002 | ADR 0001 (Notas de segurança) | RECORDED |
| SEC-003 | ADR 0006 | RECORDED |
| SEC-004 | ADR 0005 (Notas de segurança) | RECORDED |
| SEC-005 | ADR 0005 (Notas de segurança) + ADR 0006 (telemetria) | RECORDED |
| SEC-006 | ADR 0001 (Notas de segurança) | RECORDED |
| SEC-007 | ADR 0003 (Notas de segurança) | RECORDED |
| SEC-008 | ADR 0007 | RECORDED |
| SEC-009 | ADR 0006 + data-model.md (nota de partição) | RECORDED |
| SEC-010 | ADR 0007 + contracts/warmup-probe.md | RECORDED |
| SEC-011 | plan.md (Empacotamento/Release) | DEFERRED (tasks de CI no decompose) |

### SEC-001: Path traversal via `id`/`partitionKey` no provider FileSystem/JSON

- **Severidade**: High
- **Ameaça**: Tampering, Information Disclosure (STRIDE T/I)
- **Componente**: `FileSystemStore` (Python e .NET)
- **Descrição**: O modelo usa `id` e `partitionKey` como identificadores de registro (data-model.md). O provider FileSystem serializa registros em arquivos no disco. Se `id`/`partitionKey` forem usados diretamente como segmentos de caminho, valores contendo `../`, separadores de caminho, caminhos absolutos, byte nulo, ou nomes reservados (Windows: `CON`, `NUL`, `AUX`) permitem escrever/ler fora do diretório base.
- **Impacto**: Escrita/leitura arbitrária de arquivos dentro do processo host; corrupção de estado; potencial escrita sobre arquivos sensíveis do usuário no ambiente de dev.
- **Mitigação**: Nunca usar a chave crua como segmento de caminho. Derivar o nome de arquivo por hash estável (ex.: SHA-256 hex) ou por encoding reversível seguro (percent-encoding restrito a allowlist), confinar tudo a um diretório base e validar via caminho canônico (`realpath`/`Path.GetFullPath`) que o resultado permanece sob a base. Rejeitar chaves vazias ou acima de um limite de tamanho.
- **Registrar em**: ADR 0001 (Implementation Notes) + task obrigatória.

### SEC-002: JSON persistido como entrada não confiável (desserialização e arquivo corrompido)

- **Severidade**: High
- **Ameaça**: Tampering (STRIDE T)
- **Componente**: `FileSystemStore` (Python e .NET)
- **Descrição**: Ao reiniciar (CC/edge case da spec: "arquivo corrompido ou parcialmente escrito"), o provider relê JSON do disco. Dois riscos: (1) desserialização insegura/polimórfica onde o discriminador `type` do `StoreRecord` dirige instanciação arbitrária de tipos (no .NET, `TypeNameHandling` do Newtonsoft; no Python, uso de `pickle`/`eval`); (2) arquivo corrompido/parcial causando crash ou estado inconsistente.
- **Impacto**: Execução de tipo arbitrário na desserialização (RCE em cenários com gadget chains, no caso .NET/Newtonsoft mal configurado); negação de disponibilidade por arquivo malformado.
- **Mitigação**: Usar apenas desserialização segura por schema (`System.Text.Json` no .NET sem `TypeNameHandling`; módulo `json` no Python, nunca `pickle`/`eval`). Mapear `payload` por `type` contra uma allowlist explícita de discriminadores conhecidos (`tracked-session`, `warm-pool-registry`); rejeitar `type` desconhecido. Tratar arquivo corrompido/parcial com erro tipado e sem propagar exceção crua. Impor limite de tamanho de arquivo/payload.
- **Registrar em**: ADR 0001 (Implementation Notes) + task obrigatória.

### SEC-003: Vazamento de segredos e dados sensíveis via telemetria, logs e erros

- **Severidade**: High
- **Ameaça**: Information Disclosure (STRIDE I)
- **Componente**: Camada de observabilidade `stoke.*`, tratamento de erros, CredentialProvider
- **Descrição**: O plano já declara "Segredos nunca são emitidos como atributos", mas faltam guardrails concretos. Vetores: (1) connection string de fallback (contém chave) logada acidentalmente; (2) mensagens de erro do SDK Foundry ou do pipeline `Azure.Core` que embutem endpoints com chave/token; (3) `stoke.agent_session_id` listado como atributo comum de span, exposto no sink; (4) conteúdo de sessão ou payload de probe em spans.
- **Impacto**: Exposição de credenciais e de handles de capacidade (`agent_session_id`) a quem tiver acesso ao App Insights.
- **Mitigação**: Definir uma política de redação explícita: allowlist de atributos que podem ser emitidos; nunca emitir connection string, API key, token, endpoint-com-chave ou conteúdo de payload. Sanitizar mensagens de exceção antes de anexá-las a spans. Tratar `agent_session_id` como sensível (ver SEC-009). Adicionar teste que verifica ausência de padrões de segredo nos atributos emitidos.
- **Registrar em**: NOVO ADR 0006 (secrets & telemetry redaction) + tasks.

### SEC-004: Cadeia não determinística do `DefaultAzureCredential` em produção

- **Severidade**: Medium
- **Ameaça**: Spoofing, Elevation of Privilege (STRIDE S/E)
- **Componente**: `CredentialProvider`
- **Descrição**: `DefaultAzureCredential` percorre uma cadeia de credenciais; a credencial vencedora não é determinística. A documentação oficial descreve o cenário em que um `az login` no host faz a cadeia cair silenciosamente do managed identity para `AzureCliCredential`, resultando em identidade inesperada (elevação ou redução de privilégio). Fonte: learn.microsoft.com/dotnet/azure/sdk/authentication/best-practices.
- **Impacto**: Operações de control-plane executadas sob identidade não intencionada; falha silenciosa ou privilégio inesperado.
- **Mitigação**: Documentar e recomendar credencial determinística em produção: definir `AZURE_TOKEN_CREDENTIALS=prod` (ou credencial específica), ou permitir injeção de um `TokenCredential`/`ManagedIdentityCredential` explícito através do `CredentialProvider`. A abstração já permite isso; tornar explícito no contrato e na doc.
- **Registrar em**: ADR 0005 (Implementation Notes) + contrato credential-provider + task.

### SEC-005: Precedência, tempo de vida em memória e não persistência do segredo de fallback

- **Severidade**: Medium
- **Ameaça**: Information Disclosure (STRIDE I)
- **Componente**: `CredentialProvider` (fallback)
- **Descrição**: O fallback por API key/connection string deve ser tratado como opt-in explícito, subordinado ao `DefaultAzureCredential`. Riscos de design: segredo persistido no store durável, retido em memória além do necessário, ou copiado para logs de diagnóstico.
- **Impacto**: Superfície de exposição do segredo ampliada.
- **Mitigação**: Precedência explícita (preferir sempre `DefaultAzureCredential`; fallback só quando o primário indisponível). Invariante: segredos NUNCA são persistidos no store durável nem em `StoreRecord`. Minimizar o tempo de vida em memória e não expô-los em `ToString`/`repr`. Documentar que zeroização é limitada em runtimes gerenciados (best-effort com `char[]`/`SecureString` onde aplicável). Fallback vem apenas de env/config/vault (já no contrato).
- **Registrar em**: ADR 0005 + contrato credential-provider + task.

### SEC-006: Correção do file-lock cross-process (TOCTOU, locks obsoletos, starvation)

- **Severidade**: Medium
- **Ameaça**: Tampering, Denial of Service (STRIDE T/D)
- **Componente**: `FileSystemStore` (advisory lock)
- **Descrição**: O provider usa advisory lock cross-process além da concorrência otimista por etag. Riscos: (1) TOCTOU se a checagem de etag e a gravação não ocorrerem dentro do mesmo lock (janela read-modify-write); (2) lock obsoleto após crash do processo; (3) starvation/deadlock sem timeout de aquisição; (4) advisory locks não garantidos em NFS/SMB (já documentado como limitação).
- **Impacto**: Perda de atualização, corrupção de estado, ou travamento do provider.
- **Mitigação**: Realizar todo o ciclo read-check-etag-write sob o mesmo lock. Definir timeout de aquisição com erro tipado. Tratar lock obsoleto de forma segura (advisory locks do SO liberam no fim do processo; documentar o comportamento). Reafirmar que o provider é para dev local, não produção (invariante já presente).
- **Registrar em**: ADR 0001 (Implementation Notes) + task.

### SEC-007: Amplificação de custo/taxa e abuso do scheduler de warm-pool

- **Severidade**: Medium
- **Ameaça**: Denial of Service (STRIDE D) / amplificação de custo
- **Componente**: Scheduler não bloqueante (ADR 0003), `PreProvisionPoolStrategy`, `KeepaliveStrategy`
- **Descrição**: Um loop de reconciliação/reabastecimento que, sob indisponibilidade do Foundry, tente recriar sessões sem limite pode martelar o serviço e inflar custo (cada `create_session`/probe é uma operação cobrada). Gestão de custo está fora de escopo da spec, mas o vetor de abuso/DoS involuntário é de design.
- **Impacto**: Esgotamento de cota, custo inflado, rate-limiting do Foundry, degradação do host.
- **Mitigação**: Limitar `targetSize` a um máximo configurável e validado. Aplicar backoff exponencial com jitter em falhas de reconciliação, com teto de tentativas. Evitar tight loop quando o serviço está indisponível (o edge case "tamanho-alvo não atingível" já é reconhecido na spec). Emitir métrica de reabastecimento (`stoke.warmup.refill`) para detecção.
- **Registrar em**: ADR 0003 (Implementation Notes) + task.

### SEC-008: Fronteira de confiança de providers plugáveis de terceiros

- **Severidade**: Medium
- **Ameaça**: Tampering, Elevation of Privilege (STRIDE T/E)
- **Componente**: `DurableStoreProvider` plugável, `WarmupProbe` do usuário
- **Descrição**: Um provider de store de terceiros (ex.: Cosmos) e o probe do usuário implementam interfaces da Stoke e rodam in-process com plena confiança; a plataforma não os isola. Um provider malicioso poderia forjar etags (quebrando a concorrência otimista), retornar registros manipulados, ou exfiltrar estado. O modelo de confiança não está documentado.
- **Impacto**: Comprometimento da integridade do estado; falsa sensação de garantia de concorrência.
- **Mitigação**: Documentar explicitamente o modelo de confiança: providers e probes são código escolhido e confiado pela aplicação; a Stoke não os sandboxeia. Endurecer a interface: a Stoke NUNCA passa segredos/credenciais ao provider de store nem ao probe (o contrato do probe já recebe apenas `agentDefinitionId`/`agentSessionId`, o que é adequado). A Stoke valida invariantes básicas dos registros retornados (chaves não vazias, `type` na allowlist) em vez de confiar cegamente. Documentar que a garantia de concorrência otimista depende de o provider honrar o etag.
- **Registrar em**: NOVO ADR 0007 (pluggable-provider trust model) ou seção no ADR 0001 + task de documentação.

### SEC-009: `agent_session_id` como handle de capacidade sensível; partição não é fronteira de autorização

- **Severidade**: Medium
- **Ameaça**: Information Disclosure (STRIDE I)
- **Componente**: Telemetria, `TrackedSession`, modelo de partição
- **Descrição**: `agent_session_id` é um handle de capacidade: quem o possui pode referenciar/retomar a sessão. O plano o lista como atributo comum de span (`stoke.agent_session_id`), o que o expõe no sink de telemetria em nível de info. Além disso, o `partitionKey` (= `agentDefinitionId`) é particionamento lógico, não autorização (a research confirma que a isolation key é "particionamento, não autorização"); não deve ser tratado como fronteira de segurança entre tenants.
- **Impacto**: Vazamento de handles de sessão permitindo referência indevida; suposição errada de que a partição isola tenants.
- **Mitigação**: Tratar `agent_session_id` como sensível: evitar emiti-lo em plaintext em nível info; considerar omitir, truncar ou hashear em spans de baixa severidade (mantido em eventos de erro, se necessário para troubleshooting). Documentar no data-model que o modelo de partição não provê isolamento de autorização; a autorização é do control-plane do Foundry.
- **Registrar em**: NOVO ADR 0006 (redação) + nota no data-model.md.

### SEC-010: Superfície SSRF do probe e validação do endpoint

- **Severidade**: Low
- **Ameaça**: Information Disclosure / SSRF (STRIDE I)
- **Componente**: `ResponsesPingProbe`, hook do usuário
- **Descrição**: O probe embutido usa o endpoint do Foundry vindo de `FOUNDRY_PROJECT_ENDPOINT`. Se o endpoint fosse derivado de entrada não confiável, um atacante poderia redirecionar o ping (com o token anexado) para um endpoint arbitrário. No design atual o endpoint vem de env/config de confiança, o que limita o risco.
- **Impacto**: Baixo no design atual; relevante apenas se o endpoint passar a aceitar entrada não confiável.
- **Mitigação**: Reafirmar que o endpoint vem exclusivamente de configuração de confiança (env/vault), validado (esquema https, host esperado). O probe do usuário é código da aplicação (confiado); a Stoke não anexa credenciais ao invocá-lo.
- **Registrar em**: contrato warmup-probe (nota de segurança).

### SEC-011: Postura de supply-chain (pinning, SBOM, assinatura, proveniência)

- **Severidade**: Low
- **Ameaça**: Tampering (supply-chain)
- **Componente**: Empacotamento PyPI/NuGet, dependências
- **Descrição**: O beta depende de SDKs oficiais Azure. Faltam controles explícitos de proveniência e travamento de dependências.
- **Impacto**: Risco de dependência comprometida ou pacote adulterado.
- **Mitigação**: Manter minimalismo de dependências (já é um NFR). Travar/pinar dependências (lock file no Python; versões fixas no .NET). Gerar SBOM. Assinar pacotes (NuGet package signing; PyPI Trusted Publishing/provenance via OIDC). Habilitar Dependabot/scanning no repositório.
- **Registrar em**: plan.md (Empacotamento/Release) + tasks de CI.

## Requisitos de Segurança (antes de prosseguir ao decompose)

- [ ] Sanitização de path no FileSystem provider (SEC-001)
- [ ] Desserialização segura por schema + tratamento de arquivo corrompido (SEC-002)
- [ ] Política de redação de segredos/telemetria com teste de verificação (SEC-003)
- [ ] Credencial determinística em produção documentada e suportada (SEC-004)
- [ ] Invariante: segredos nunca persistidos no store; precedência de fallback (SEC-005)
- [ ] Ciclo read-modify-write sob lock + timeout de aquisição (SEC-006)
- [ ] Backoff/jitter e teto de `targetSize` no scheduler (SEC-007)
- [ ] Modelo de confiança de providers documentado + hardening de interface (SEC-008)
- [ ] `agent_session_id` tratado como sensível na telemetria (SEC-009)

## Decisões de ADR com Implicações de Segurança

| ADR | Decisão | Implicação | Recomendação |
|-----|---------|------------|--------------|
| 0001 | FileSystem/JSON com file-lock | Path traversal, desserialização, TOCTOU | Atualizar Implementation Notes (SEC-001, SEC-002, SEC-006) |
| 0005 | DefaultAzureCredential + fallback | Cadeia não determinística; ciclo de vida do segredo | Atualizar (SEC-004, SEC-005) |
| 0003 | Scheduler não bloqueante | Amplificação de custo/taxa | Atualizar (SEC-007) |
| NOVO 0006 | Secrets & telemetry redaction | Vazamento de segredos e handles | Criar ADR (SEC-003, SEC-005, SEC-009) |
| NOVO 0007 | Pluggable-provider trust model | Confiança in-process de terceiros | Criar ADR (SEC-008) |

## Boas práticas já presentes no design

- Fronteira control-plane-only reduz drasticamente a superfície (sem cliente de data-plane, ADR 0002).
- Contrato do probe recebe apenas identificadores, não credenciais (bom isolamento).
- Segredos de fallback vindos de env/config/vault, nunca hardcoded (contrato credential-provider).
- Core sem dependência de SDK de store de produção reduz superfície e supply-chain.
- Concorrência otimista por etag como fonte de verdade de conflito.
- Declaração explícita de que segredos não são emitidos como atributos de telemetria (precisa de guardrail, SEC-003).

## Reasoning Log

- **Veredito APPROVED_WITH_CONTROLS**: nenhum achado Critical; os três High são controles preventivos de design-time, aplicáveis antes da implementação sem retrabalho, pois não há código.
- **Ameaças descartadas**: injeção SQL/NoSQL (sem query arbitrária; interface é CRUD + query-por-partição, ADR 0001); XSS/CSRF (sem UI nem endpoints HTTP expostos pela Stoke); autenticação de usuários finais (delegada ao Foundry/Entra; a Stoke não implementa authz própria); dual-protocol client (fora de escopo, ADR 0002).
- **SSRF rebaixado a Low (SEC-010)**: no design atual o endpoint vem de config de confiança; só sobe de severidade se passar a aceitar entrada não confiável.
