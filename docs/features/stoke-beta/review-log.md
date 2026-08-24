# Review: Stoke Beta (implementação Python + suíte de conformidade)

- **Data**: 2026-08-24
- **Revisor**: devsquad.review (contexto independente; não implementou o código)
- **Branch**: feat/stoke-beta-foundation (commitada e pushada para origin)
- **Artefatos validados**: spec.md (v1.2), plan.md, data-model.md, contracts/*, security-review-architecture.md (SEC-001..SEC-011), ADRs 0001-0007, coding-guidelines
- **Código revisado**: python/src/foundry_stoke/** (22 arquivos), conformance/fixtures/** (5), python/tests/**

## Resultado

**Status**: PASSED_WITH_FINDINGS
- Critical: 0
- High: 1
- Medium: 3
- Low: 4
- Suggestion: 1

**Recomendação go/no-go para paridade .NET**: **GO condicional**. Nenhum achado Critical; a fronteira control-plane, a ausência de acoplamento a SDK de store e os controles de segurança estão implementados e testados. Resolver os achados High-1 e Medium-2 (semânticas cross-language de credencial e de tradução de status) **antes** de codificar o .NET, para que ambas as linguagens encodem o mesmo contrato corrigido em vez de divergir.

## Verificação de build/testes (independente)

| Comando | Resultado |
|---------|-----------|
| `pytest -q` | PASS (98 testes) |
| `mypy --strict src` | PASS (22 arquivos, sem erros) |
| `ruff check .` | PASS |

## Conformidade de fronteira e ADR

| Item | Status | Evidência |
|------|--------|-----------|
| ADR 0002 control-plane-only (sem cliente data-plane vazado) | HONRADO | Único toque de data-plane é o `ResponsesPingProbe` mínimo/opcional com client injetado (permitido pelo ADR 0002). Imports azure são lazy (foundry_adapter, config._build_client, credential_provider). |
| CC-004 core sem SDK de Cosmos | PASS | test_no_cosmos_dependency assegura que nenhum `azure.cosmos`/`tables`/`redis`/`pymongo` é carregado ao importar o core. |
| ADR 0003 scheduler não bloqueante | HONRADO | Único `time.sleep` está dentro de `_record_lock`, executado em `asyncio.to_thread`; toda I/O do FileSystem é offloaded; loops de warm-up e clock são awaitable; VirtualClock evita sleeps reais. |
| ADR 0004 prontidão cross-language | NO CAMINHO | Superfície pública idiomática e portável; taxonomia de erro neutra mapeada nas fixtures; fixtures JSON compartilhadas são o contrato. Ver ressalva High-1. |
| FR US1-US4 cobertos pela superfície pública | SIM | Ciclo de sessão (incl. erro determinístico em sessão encerrada), CRUD+query+concorrência otimista, pool+keepalive com probe plugável, auth primário+fallback+erro. US5 harness presente. |

## Verificação por controle de segurança (SEC-00x)

| Controle | Status | Observação |
|----------|--------|------------|
| SEC-001 sanitização de path | IMPLEMENTADO | Hash SHA-256 de id e partition_key; verificação de caminho canônico sob a base; chave vazia/oversized rejeitada; teste de traversal confinado. |
| SEC-002 desserialização segura + allowlist + tamanho | IMPLEMENTADO | Apenas `json`; allowlist de `type` no read **e** write; corrompido/parcial/oversized -> erro tipado; testado. |
| SEC-003/009 redação de telemetria | IMPLEMENTADO | Allowlist por construção; `agent_session_id` hasheado em info e plaintext só em error; mensagem de exceção sanitizada; testado. |
| SEC-004 credencial determinística em prod | IMPLEMENTADO | Injeção de `TokenCredential`; `AZURE_TOKEN_CREDENTIALS` documentado. |
| SEC-005 fallback: precedência + não persistido + repr mascarado | PARCIAL | Manuseio do segredo (slots, repr/str mascarados, leitura no resolve, nunca persistido) IMPLEMENTADO. A semântica de precedência/failover diverge (ver High-1). |
| SEC-006 ciclo RMW sob lock + timeout + escrita atômica | IMPLEMENTADO (POSIX) / PARCIAL (não-POSIX) | POSIX: fcntl, lock único cobrindo read-check-etag-write, `os.replace` atômico, timeout de aquisição. Não-POSIX: lock obsoleto após crash (ver Medium-3). |
| SEC-007 teto de target + backoff/jitter + teto de retries | IMPLEMENTADO | Teto validado; backoff exponencial com full jitter; `max_retries` interrompe o loop; métrica `stoke.warmup.refill`. |
| SEC-008 validate_record_invariants em registros retornados | IMPLEMENTADO | Aplicado em `PreProvisionPoolStrategy._load_registry`; o FileSystem já impõe a allowlist no `_read_file`. |
| SEC-010 validação de endpoint + sem credenciais ao probe do usuário | IMPLEMENTADO | `validate_endpoint` (https + host) no probe e no config; `CallableProbe` passa só ids; `ResponsesPingProbe` captura exceções sem vazar. |
| SEC-011 supply-chain | DEFERRED | Tasks de CI no decompose, conforme registrado. |

## Achados

### High

- **REV-001 — Semântica de fallback de credencial equipara "indisponível" a "pacote não instalado".**
  - **Status: RESOLVIDO (2026-08-24).** Ver "Resolução" abaixo.
  - Esperado (CC-005, FR-020, contracts/credential-provider.md "Renovação... se falhar e houver fallback, usa o fallback", ADR 0005/SEC-005 "o fallback só é usado quando o primário está indisponível"): quando o `DefaultAzureCredential` não consegue emitir token em runtime e há fallback configurado, a resolução usa o fallback.
  - Encontrado: `resolve_credential` retorna `DefaultAzureCredential()` assim que `import azure.identity` tem sucesso, sem tentar adquirir token. Com o pacote instalado (norma em produção), o fallback por API key/connection string **nunca** é usado, mesmo que o Entra ID esteja indisponível. O failover de runtime não existe; `resolve_credential` é síncrono e não faz `get_token`, logo não detecta indisponibilidade.
  - Arquivo: [python/src/foundry_stoke/auth/credential_provider.py](python/src/foundry_stoke/auth/credential_provider.py#L104-L128)
  - Relacionado (qualidade de teste): a fixture `auth-probe.json` modela `primary_available: false` como ausência do módulo (`ImportError`), então a suíte passa afirmando um contrato mais fraco do que o CC-005 descreve. Ver [conformance/fixtures/auth-probe.json](conformance/fixtures/auth-probe.json).
  - Correção sugerida: (a) implementar failover de runtime (tentar aquisição de token, capturar `CredentialUnavailableError`, então cair para o fallback); ou (b) estreitar explicitamente o contrato/CC-005 e documentar a limitação no código e no ADR 0005. Decidir **antes** da paridade .NET para que as duas linguagens compartilhem uma única semântica.
  - **Resolução (2026-08-24)**: adotada a opção (a) com um seam explícito e não bloqueante por padrão. `CredentialProvider` ganhou `entra_credential_factory` (fábrica injetável do primário; o default constrói `DefaultAzureCredential` lazy) e `token_probe` (hook opcional de aquisição de token). "Primário indisponível" agora significa a fábrica lançar (qualquer exceção, não só `ImportError`) ou o `token_probe` lançar; em ambos, a resolução cai para o fallback sem vazar a exceção. Precedência: injetada > primário > API key > connection string > `NoCredentialAvailable`. SEC-004/SEC-005 preservados (segredo lido no resolve, não persistido, mascarado em repr/str). A fixture `auth-probe.json` foi reescrita para afirmar a precedência comportamental (primário disponível/indisponível via fábrica, ordem api_key-antes-de-connection_string, failover por `token_probe`), não mais `ImportError`. Contrato e ADR 0005 atualizados. 107 pytest, ruff, mypy --strict limpos.

### Medium

- **REV-002 — Tradutor de status default-para-Active pode mascarar estados desconhecidos.**
  - **Status: RESOLVIDO (2026-08-24).** Ver "Resolução" abaixo.
  - Esperado (FR-002, CC-001): refletir transições Active -> Idle -> Resumed.
  - Encontrado: `default_status_translator` mapeia qualquer status não reconhecido para `SessionState.ACTIVE`. Como as strings oficiais eram um gap de pesquisa não confirmado, em produção uma sessão realmente Idle/Resumed seria reportada como Active, quebrando silenciosamente a reflexão de estado. A isolação (tradutor injetável) é boa; o default otimista é o risco.
  - Arquivo: [python/src/foundry_stoke/session/controller.py](python/src/foundry_stoke/session/controller.py#L71-L79)
  - Correção sugerida: para status desconhecido, levantar erro ou emitir um `UNKNOWN` distinto com log de erro, forçando a confirmação das strings reais (NEEDS RESEARCH) antes do GA; manter o override injetável. Decidir a política cross-language antes do .NET.
  - **Resolução (2026-08-24)**: gap de pesquisa fechado — a taxonomia oficial `AgentSessionStatus` foi confirmada (spec v1.3, data-model.md, ADR 0002 Implementation Note): `creating`, `active`, `idle`, `updating`, `failed`, `deleting`, `deleted`, `expired`. `SessionState` foi redefinido para esses oito valores (strings minúsculas) mais um `UNKNOWN` de fallback. O `default_status_translator` agora mapeia case-insensitive sobre os oito valores oficiais e qualquer valor não reconhecido ou futuro vira `SessionState.UNKNOWN`, nunca coagido para `ACTIVE` (removido o default-para-Active); o tradutor segue injetável. "Resumed" deixou de ser um status: é a transição derivada `idle` -> `active`, refletida pelo controller via `resumed_at` (memória mínima e determinística do último estado observado por sessão). O warm pool passou a evictar estados terminais (`FAILED`, `EXPIRED`, `DELETED`, `DELETING`) e a não contar `UNKNOWN` como pronto; `IDLE` permanece candidato a keepalive/reprovisionamento. As fixtures de conformance (session-lifecycle.json, warmup.json) foram atualizadas para codificar a semântica corrigida (CC-001 resume derivado, CC-008 unknown, taxonomia case-insensitive, eviction terminal/unknown) como fonte única cross-language para o futuro harness .NET. 129 pytest, ruff, mypy --strict limpos.

- **REV-003 — Fallback de lock não-POSIX deixa lock obsoleto após crash.**
  - Encontrado: no branch sem `fcntl`, o lock via `O_CREAT|O_EXCL` não é liberado automaticamente na morte do processo (ao contrário de `fcntl.flock`). Um crash no meio de uma escrita deixa um `.lock` permanente e as operações seguintes falham com `LockTimeout` até remoção manual. A docstring "advisory locks release at process end" só vale no caminho fcntl.
  - Arquivo: [python/src/foundry_stoke/store/file_system.py](python/src/foundry_stoke/store/file_system.py#L112-L133)
  - Impacto: provider é dev-local, mas dev em Windows é afetado.
  - Correção sugerida: documentar o comportamento de lock obsoleto e/ou reclamar lock obsoleto por mtime no fallback.

- **REV-004 — Aresta de concorrência otimista: upsert com etag não nulo em registro ausente cria silenciosamente.**
  - Encontrado: em InMemory e FileSystem, `upsert(record, expected_etag=<stale>)` sobre um registro inexistente cria o registro em vez de conflitar. Se um registro for deletado concorrentemente, um upsert em voo com o etag antigo o **ressuscita** (delete perdido). CC-003 cobre escritas concorrentes, mas não delete-seguido-de-upsert-stale.
  - Arquivos: [python/src/foundry_stoke/store/in_memory.py](python/src/foundry_stoke/store/in_memory.py#L59-L73), [python/src/foundry_stoke/store/file_system.py](python/src/foundry_stoke/store/file_system.py#L200-L221)
  - Correção sugerida: quando `expected_etag` não for None e o registro estiver ausente, levantar `ConcurrencyConflict` (o chamador esperava uma versão existente).

### Low

- **REV-005 — get_session/list_sessions não preservam o idle_timeout configurado no create.** Reemitem `DEFAULT_IDLE_TIMEOUT_SECONDS`; `TrackedSession` é snapshot sem estado; o controller não mantém registro de sessões. Documentar ou carregar o valor. [python/src/foundry_stoke/session/controller.py](python/src/foundry_stoke/session/controller.py#L120-L156)
- **REV-006 — idle_timeout é validado e carregado localmente, mas não propagado à plataforma.** `FoundrySessionOperations.create_session` o ignora (por design da plataforma: idle_timeout é definido na criação da versão do agente). Documentado no comentário do adapter; garantir que a doc de usuário deixe claro que é advisory. [python/src/foundry_stoke/session/foundry_adapter.py](python/src/foundry_stoke/session/foundry_adapter.py#L46-L57)
- **REV-007 — JSON persistido no FileSystem usa nomes snake_case (partition_key, created_at).** Como os providers são por linguagem e não compartilhados em runtime, não quebra a conformidade (o contrato são as fixtures). Ressalva de interop futura apenas se o formato em disco for compartilhado. [python/src/foundry_stoke/models.py](python/src/foundry_stoke/models.py#L66-L79)
- **REV-008 — query_by_partition (FileSystem) aborta a query inteira se um único arquivo estiver corrompido.** Fail-safe, mas um arquivo ruim bloqueia listar a partição toda. Decidir intencionalmente entre pular/reportar corrompidos vs falhar a query. [python/src/foundry_stoke/store/file_system.py](python/src/foundry_stoke/store/file_system.py#L232-L243)

### Suggestion

- **REV-009 — `PreProvisionPoolStrategy.reconcile` não captura `ConcurrencyConflict` no `_save_registry`.** Dois reconciladores concorrentes para o mesmo agente exporiam um conflito. O design de loop único torna isso improvável; anotar para quando múltiplos schedulers por agente forem possíveis. [python/src/foundry_stoke/warmup/pool.py](python/src/foundry_stoke/warmup/pool.py#L169-L186)

## Status dos gaps de pesquisa (isolados com segurança?)

- **Strings do enum de status**: RESOLVIDO (2026-08-24). Taxonomia oficial `AgentSessionStatus` confirmada e adotada (oito valores + `UNKNOWN`); tradução case-insensitive; valor desconhecido/futuro -> `UNKNOWN`, nunca coagido. "Resumed" é transição derivada (`resumed_at`), não status. Ver REV-002.
- **Payload de keepalive Responses**: isolado no adapter `ResponsesPingProbe`; não inventado ao longo do código; `CallableProbe` cobre containers customizados. Seguro, não mascara bugs.
- **Reset do idle_timer / não propagação do idle_timeout**: assunção documentada; baixo impacto (REV-006).

## Qualidade de testes

- Testes são behavior-focused pela superfície pública (store via provider, controller via port fake, credencial via env/injeção, telemetria via funções públicas). Bom.
- Conformance harness dirige a API pública e afirma o contrato neutro por domínio; fixtures são a fonte única de verdade para o .NET. Bom.
- Cobertura de arestas: arquivo corrompido/parcial, oversized, lock timeout, conflito de concorrência, idle timeout inválido, sem credencial. Boa.
- Ponto fraco: a fixture de auth modela "primário indisponível" como `ImportError`, afirmando um contrato mais fraco que o CC-005 (ligado a REV-001).

## Learning Insights

- **Disponibilidade de credencial não é o mesmo que import de pacote.** `DefaultAzureCredential` é lazy: construir não valida nada; a falha só aparece na primeira aquisição de token (rede). Equiparar "primário indisponível" a "módulo ausente" produz um failover que nunca dispara no ambiente onde ele mais importa (produção, com o pacote instalado). Failover de credencial precisa acontecer em torno da aquisição de token, não da importação. Referência: contracts/credential-provider.md, ADR 0005/SEC-005; learn.microsoft.com/dotnet/azure/sdk/authentication/best-practices.
- **Um default otimista em fronteira de tradução esconde a falha em vez de expô-la.** Mapear status desconhecido para Active troca uma falha barulhenta (que forçaria confirmar as strings reais) por um estado silenciosamente errado que só se manifesta em produção. Em fronteiras com contrato desconhecido, prefira falhar/registrar explicitamente sobre assumir o caminho feliz. Referência: research.md (gap do enum de status), FR-002/CC-001.

## Próximos passos

MUST antes da paridade .NET (fixar contrato cross-language único):
- REV-001 (semântica de disponibilidade/failover de credencial) e atualizar a fixture CC-005. **RESOLVIDO (2026-08-24).**
- REV-002 (política de status desconhecido). **RESOLVIDO (2026-08-24)** — taxonomia oficial adotada; unknown -> `UNKNOWN`; resume derivado; eviction terminal no warm pool; fixtures atualizadas.

Pode aguardar (rastrear como issues):
- REV-003 (lock obsoleto não-POSIX, dev-only), REV-004 (ressurreição por upsert), REV-005/006 (doc de idle_timeout), REV-007 (casing em disco), REV-008 (query com corrompido), REV-009 (conflito no save do pool), SEC-011 (tasks de CI).
