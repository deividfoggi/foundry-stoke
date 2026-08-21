# secrets-telemetry-redaction

**Status**: Proposed
**Date**: 2026-08-21

## Context

A Stoke emite telemetria via OpenTelemetry (spans, métricas, eventos de erro) integrável ao Application Insights. O plano já declara que "segredos nunca são emitidos como atributos", mas falta um guardrail concreto que garanta essa propriedade. O security-review (2026-08-21) identificou vetores de vazamento: connection string de fallback (contém chave) logada acidentalmente; mensagens de erro do SDK Foundry ou do pipeline `Azure.Core` que embutem endpoints com chave/token; `stoke.agent_session_id` listado como atributo comum de span, exposto no sink em nível de info; e conteúdo de sessão ou payload de probe em spans.

Além disso, `agent_session_id` é um handle de capacidade: quem o possui pode referenciar/retomar a sessão. O modelo de partição (`partitionKey = agentDefinitionId`) é particionamento lógico, não fronteira de autorização, o que reforça que esses identificadores não devem ser tratados como públicos.

Esta decisão define a política de redação da telemetria da Stoke. Cobre SEC-003, o aspecto de telemetria do SEC-005 e SEC-009.

## Priorities and Requirements (ordered)

1. **Nenhum segredo cruza a fronteira de telemetria** — Connection string, API key, token, endpoint-com-chave e conteúdo de payload/sessão MUST NOT aparecer em nenhum atributo, evento ou mensagem emitida. Verificável por teste automatizado que falha se padrões de segredo aparecem nos atributos.
2. **Handles sensíveis tratados como sensíveis** — `agent_session_id` é um handle de capacidade; sua emissão em plaintext em nível de info deve ser evitada, sem impedir troubleshooting de erros.
3. **Determinismo e simplicidade da política** — A regra precisa ser previsível e difícil de burlar por engano de um contribuidor; falha segura (o que não está explicitamente permitido não é emitido).
4. **Observabilidade útil preservada** — A redação não pode esvaziar a telemetria a ponto de inviabilizar diagnóstico de falhas legítimas.

## Options Considered

### Option 1: Allowlist de atributos emitíveis + sanitização de exceções + tratamento de handles

Definir uma **allowlist** explícita dos atributos que podem ser emitidos (o conjunto estável `stoke.*` do plano: `agent_definition_id`, `session.state`, `store.provider`, `warmup.strategy`, etc.). Qualquer atributo fora da allowlist não é emitido. Mensagens de exceção são sanitizadas antes de anexadas a spans (remover endpoints-com-chave/tokens). `agent_session_id` é tratado como sensível: omitido, truncado ou hasheado em spans de baixa severidade, retido em eventos de erro quando necessário para troubleshooting. Um teste assert que nenhum padrão de segredo (connection string, chave, token) aparece nos atributos emitidos.

**Evaluation against priorities**:
- **Nenhum segredo cruza**: Atende. Falha segura por construção: só o que está na allowlist é emitido; segredos nunca entram na lista.
- **Handles sensíveis**: Atende. Regra explícita de omissão/truncamento/hash de `agent_session_id` por nível de severidade.
- **Determinismo e simplicidade**: Atende. Allowlist é auditável e um novo atributo exige adição consciente à lista (barreira contra vazamento acidental).
- **Observabilidade útil**: Atende. O conjunto `stoke.*` do plano permanece; erros retêm o contexto necessário.

### Option 2: Denylist (redação por padrões de segredo conhecidos)

Emitir tudo por padrão e aplicar uma denylist/regex que redige padrões conhecidos de segredo (chaves, connection strings, tokens) antes da exportação.

**Evaluation against priorities**:
- **Nenhum segredo cruza**: Parcial/Falha. Denylist é falha insegura: um segredo em formato novo/não previsto passa. Não há garantia de completude dos padrões.
- **Handles sensíveis**: Parcial. `agent_session_id` não casa com padrão de segredo genérico; exigiria regra dedicada de qualquer forma.
- **Determinismo e simplicidade**: Falha. Manter regex de segredos atualizada é frágil e propenso a falso-negativo.
- **Observabilidade útil**: Atende (ortogonal).

## Decision

Adotar a **Option 1**: política de redação por **allowlist** de atributos emitíveis. NUNCA emitir connection string, API key, token, endpoint-com-chave ou conteúdo de payload/sessão. Sanitizar mensagens de exceção antes de anexá-las a spans. Tratar `agent_session_id` como sensível: omitir, truncar ou hashear em nível de info, retendo-o em eventos de erro apenas quando necessário para troubleshooting. Exigir um teste automatizado que asserta a ausência de padrões de segredo nos atributos emitidos.

Justificativa ancorada nas prioridades: a Option 1 é a única que satisfaz a prioridade 1 (falha segura) e a prioridade 3 (determinismo) sem depender da completude de uma lista de padrões. A Option 2 é falha insegura na prioridade 1 e frágil na prioridade 3. O conjunto estável `stoke.*` definido no plano preserva a prioridade 4.

## Implementation Notes

- A allowlist canônica de atributos é o conjunto `stoke.*` documentado em `docs/features/stoke-beta/plan.md` (seção Observabilidade). Qualquer novo atributo exige adição consciente à allowlist.
- O teste de verificação (prioridade 1) é obrigatório: dado um conjunto de spans/eventos emitidos, nenhum valor deve casar padrões de connection string/API key/token. A implementação como task pertence ao decompose.
- `agent_session_id`: preferir hash/truncamento em spans de info; manter íntegro apenas em eventos de erro quando necessário ao diagnóstico. Ver nota correlata no data-model.md sobre o modelo de partição não ser fronteira de autorização (SEC-009).
- Custo de infraestrutura: nenhum (política aplicada no processo, antes da exportação).

## References

* docs/features/stoke-beta/security-review-architecture.md (SEC-003, SEC-005, SEC-009)
* docs/features/stoke-beta/plan.md (Observabilidade; conjunto `stoke.*`)
* docs/features/stoke-beta/data-model.md (modelo de partição; `agentSessionId`)
* docs/architecture/decisions/0005-authentication-strategy.md (segredos de fallback)
