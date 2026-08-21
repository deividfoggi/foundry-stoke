# pluggable-provider-trust-model

**Status**: Proposed
**Date**: 2026-08-21

## Context

A Stoke expõe interfaces plugáveis: `DurableStoreProvider` (um provider de terceiros, por exemplo Cosmos, implementa a persistência sem alterar o core — ADR 0001) e `WarmupProbe` (o hook do usuário para Invocations/containers customizados — ADR 0003). Ambos implementam interfaces da Stoke e rodam **in-process** com plena confiança; a plataforma não os isola nem os sandboxeia.

O security-review (2026-08-21) observou que um provider de store malicioso ou defeituoso poderia forjar etags (quebrando a concorrência otimista), retornar registros manipulados ou exfiltrar estado, e que esse modelo de confiança não estava documentado. Esta decisão formaliza o modelo de confiança para código plugável. Cobre SEC-008 e reforça SEC-010 (validação do endpoint do probe).

## Priorities and Requirements (ordered)

1. **Modelo de confiança explícito e honesto** — Deixar claro, para quem integra, que providers e probes são código escolhido e confiado pela aplicação; a Stoke não os isola. Evitar uma falsa sensação de sandbox.
2. **Não vazar segredos para código plugável** — A Stoke MUST NOT passar segredos/credenciais ao provider de store nem ao probe. Superfície mínima de acoplamento.
3. **Não confiar cegamente em dados retornados** — Validar invariantes básicas dos registros retornados por um provider (chaves não vazias, `type` na allowlist) em vez de aceitar qualquer estrutura.
4. **Simplicidade proporcional ao risco** — O beta é uma biblioteca sem multi-tenant hostil in-process; a solução não deve introduzir isolamento pesado (processo separado, IPC) desproporcional ao risco real.

## Options Considered

### Option 1: Modelo de confiança documentado (document-only)

Documentar que providers e probes rodam in-process com plena confiança e que a Stoke não os sandboxeia; nenhuma verificação em runtime sobre o que retornam.

**Evaluation against priorities**:
- **Modelo explícito**: Atende. O contrato de confiança fica claro.
- **Não vazar segredos**: Parcial. Depende só de disciplina; sem barreira concreta reafirmada nos contratos.
- **Não confiar cegamente**: Falha. Sem validação, a Stoke aceita etags forjados e registros malformados, quebrando a garantia de concorrência otimista silenciosamente.
- **Simplicidade**: Atende.

### Option 2: Endurecimento por isolamento (sandbox/processo separado)

Executar providers/probes em processo separado ou sandbox, com IPC e verificação de fronteira.

**Evaluation against priorities**:
- **Modelo explícito**: Atende, mas muda a natureza da biblioteca.
- **Não vazar segredos**: Atende, ao custo de complexidade.
- **Não confiar cegamente**: Atende parcialmente (a fronteira ajuda), mas exige serialização/validação pesada.
- **Simplicidade**: Falha. Isolamento de processo/IPC é desproporcional a uma biblioteca de control-plane cujo código plugável é escolhido pela própria aplicação.

### Option 3: Confiança documentada + hardening leve de interface (both)

Documentar o modelo de confiança in-process (como Option 1) **e** endurecer a interface de forma leve: a Stoke nunca passa segredos ao provider/probe e valida invariantes básicas dos registros retornados (chaves não vazias, `type` na allowlist). Reafirmar que a garantia de concorrência otimista depende de o provider honrar o etag.

**Evaluation against priorities**:
- **Modelo explícito**: Atende. Confiança in-process documentada.
- **Não vazar segredos**: Atende. Invariante reafirmado nos contratos (o probe já recebe apenas `agentDefinitionId`/`agentSessionId`).
- **Não confiar cegamente**: Atende. Validação de invariantes básicas detecta registros malformados; a dependência do etag fica documentada.
- **Simplicidade**: Atende. Hardening é validação barata em runtime, sem isolamento de processo.

## Decision

Adotar a **Option 3**: modelo de confiança in-process documentado, combinado com hardening leve de interface. Providers de store de terceiros e probes de warm-up fornecidos pelo usuário rodam in-process com plena confiança; a Stoke NÃO os sandboxeia. A Stoke NUNCA passa segredos/credenciais ao provider de store nem ao probe. A Stoke valida invariantes básicas dos registros retornados (chaves não vazias, `type` na allowlist de discriminadores) em vez de confiar cegamente. Fica documentado que a garantia de concorrência otimista depende de o provider honrar o etag.

Para o probe (SEC-010): o endpoint vem exclusivamente de configuração de confiança (env/vault), validado (esquema https, host esperado); a Stoke não anexa credenciais ao invocar um probe do usuário.

Justificativa ancorada nas prioridades: a Option 3 satisfaz a prioridade 3 (não confiar cegamente) que a Option 1 falha, sem incorrer na complexidade desproporcional da Option 2 (prioridade 4). A allowlist de `type` reusa a mesma definida no ADR 0001/0006, mantendo consistência.

## Implementation Notes

- A allowlist de `type` (`tracked-session`, `warm-pool-registry`) é a mesma do ADR 0001 (desserialização segura) e do ADR 0006 (redação). Manter uma única fonte de verdade.
- Validação de invariantes de registro retornado: chave `id`/`partitionKey` não vazia, `type` na allowlist. Registro que falha a validação resulta em erro tipado, não em aceitação silenciosa.
- Probe (SEC-010): validar `https` e host esperado do endpoint de configuração; nunca anexar credenciais ao invocar um probe do usuário. Ver contracts/warmup-probe.md.
- Custo de infraestrutura: nenhum (validação in-process).

## References

* docs/features/stoke-beta/security-review-architecture.md (SEC-008, SEC-010)
* docs/architecture/decisions/0001-durable-store-provider.md (allowlist de `type`, etag)
* docs/architecture/decisions/0003-warmup-strategies-scheduler.md (probe)
* docs/features/stoke-beta/contracts/warmup-probe.md
* docs/features/stoke-beta/contracts/durable-store-provider.md
