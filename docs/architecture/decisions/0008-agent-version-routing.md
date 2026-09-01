# agent-version-routing

**Status**: Proposed
**Date**: 2026-08-25

## Context

O Foundry cria cada sessão de agente ancorada numa versão publicada, via o `version_indicator` obrigatório (`VersionRefIndicator(agent_version=...)`). A versão é, portanto, um parâmetro **por sessão** no control-plane.

No beta, a Stoke trata a versão de forma limitada:

- `SessionController.create_session(agent_definition_id, ...)` não recebe versão. Quem a resolve é o adapter `FoundrySessionOperations`, que aceita um `agent_version` opcional **por instância de adapter** (pin único) ou, quando ausente, resolve a versão publicada mais recente por agente em tempo de create (`list_versions` em ordem decrescente, primeiro item).
- O warm pool (`PreProvisionPoolStrategy`) é chaveado **apenas por agente**: `registry_id = "warm-pool:{agent_definition_id}"`, e `tracked_session_ids` é uma lista plana sem dimensão de versão.

Duas consequências decorrem disso e motivam esta decisão:

1. No modo default (latest), um mesmo pool pode acumular sessões de **versões diferentes do mesmo agente** ao longo do tempo (quando uma nova versão é publicada, os próximos creates migram para ela enquanto as sessões antigas permanecem). O pool não registra nem observa isso: é drift silencioso.
2. Não é possível manter **dois pools do mesmo agente em versões distintas** (por exemplo, estável com alvo alto e canary com alvo baixo), porque ambos colidiriam no mesmo `registry_id` e na mesma partição do store.

Cenários futuros de rollout progressivo (A/B, canary, blue/green) dependem de escolher a versão por sessão e de manter pools segregados por versão. Esta decisão define **o modelo de versão da Stoke** e o caminho de evolução, sem implementar agora.

## Priorities and Requirements (ordered)

1. **Evolução aditiva da superfície pública** — Decidir onde a versão mora afeta a API. O modelo escolhido deve permitir suportar multi-versão no futuro apenas com adições retrocompatíveis (parâmetros opcionais, campos opcionais), sem quebrar quem já usa `create_session`, `TrackedSession` ou o warm pool.
2. **Habilitar A/B e canary sem violar a fronteira control-plane (ADR 0002)** — A Stoke deve prover o mecanismo (roteamento de versão por sessão, pools por versão, telemetria de ciclo de vida por versão). A decisão de qualidade que promove ou reverte um canary usa os sinais de avaliação e trace do Foundry (data-plane) e permanece fora da Stoke.
3. **Comportamento de versão observável e determinístico** — Eliminar o drift silencioso. Quando o pool contém sessões de mais de uma versão, isso deve ser intencional, visível na telemetria e no estado durável, nunca um efeito colateral.
4. **Simplicidade proporcional ao beta** — Não implementar multi-versão agora. Travar os seams e o data model o suficiente para que a implementação futura seja aditiva; não introduzir política de roteamento no core antes de haver demanda concreta.

## Options Considered

### Option 1: Manter o status quo (versão fora do pool, pin por adapter)

A versão continua sendo resolvida pelo adapter (pin único ou latest). O warm pool permanece chaveado por agente. Documenta-se a limitação; nenhuma mudança de contrato.

**Evaluation against priorities**:
- **Evolução aditiva**: Parcial. Não fecha portas, mas também não reserva espaço para versão na API nem no registro; a mudança futura ainda precisa reintroduzir a dimensão de versão.
- **Habilitar A/B e canary**: Falha. A colisão de `registry_id` impede pools por versão; não há roteamento por sessão.
- **Determinístico**: Falha. O modo latest produz drift silencioso; o modo pin esconde o problema fixando tudo numa versão por instância.
- **Simplicidade**: Atende. Custo zero agora.

### Option 2: Versão como dimensão de primeira classe (implementação deferida)

Reserva-se a versão como dimensão explícita, com implementação adiada:

- `create_session(agent_definition_id, *, agent_version=None, ...)` — versão opcional por chamada.
- `TrackedSession.agent_version` — a sessão registra a versão que a respalda.
- Warm pool chaveado por `(agent, version)` — `registry_id` inclui a versão; pools segregados coexistem.
- Seam `VersionSelector` (política plugável) com built-ins simples (`Latest`, `Fixed`); `Weighted` (A/B) e `Canary` como incrementos posteriores.
- Telemetria de ciclo de vida tagueada por `agent_version`.
- Atribuição estável (sticky) reusa o `DurableStoreProvider` existente para mapear uma chave lógica a uma versão.

**Evaluation against priorities**:
- **Evolução aditiva**: Atende. Todos os pontos são adições retrocompatíveis (parâmetros e campos opcionais, novo `registry_id` com migração de registro).
- **Habilitar A/B e canary**: Atende. Pools por versão, roteamento por sessão e telemetria por versão são exatamente o mecanismo necessário; a decisão de qualidade permanece no Foundry.
- **Determinístico**: Atende. A versão passa a ser explícita no create, no `TrackedSession` e no registro do pool; o drift vira uma escolha visível.
- **Simplicidade**: Atende, porque a implementação é deferida. Agora só se fixa a direção; o beta não ganha código novo.

### Option 3: Roteamento de versão inteiramente fora da Stoke

A Stoke nunca lida com versão além de um pin opaco. A aplicação decide toda a versão e a passa via DI, mantendo múltiplas instâncias de Stoke, uma por versão.

**Evaluation against priorities**:
- **Evolução aditiva**: Atende, por omissão (a Stoke não muda).
- **Habilitar A/B e canary**: Parcial. É possível na aplicação, mas a Stoke não oferece pools por versão nem telemetria por versão; cada instância teria seu próprio store e o operador reconstrói correlação por fora. Ergonomia fraca e sem visão unificada.
- **Determinístico**: Parcial. Determinístico por instância, mas sem consciência de versão na telemetria/estado agregado.
- **Simplicidade**: Parcial. Simples no core, complexo no consumo (N instâncias, N stores, correlação manual).

## Decision

Adotar a **Option 2** como direção-alvo do modelo de versão da Stoke, com **implementação deferida** (fora do beta atual). A versão passa a ser uma dimensão de primeira classe: opcional por chamada em `create_session`, registrada em `TrackedSession.agent_version`, e parte da chave do warm pool `(agent, version)`; a seleção de versão é uma política plugável (`VersionSelector`) separada do mecanismo, e a decisão de qualidade do canary permanece no Foundry (ADR 0002).

A escolha atende à prioridade 1 (todas as mudanças são aditivas e retrocompatíveis, então o beta sai sem arrependimento), à prioridade 2 (o mecanismo habilita A/B e canary sem cruzar a fronteira control-plane) e à prioridade 3 (elimina o drift silencioso ao tornar a versão explícita). A prioridade 4 é respeitada porque nada é implementado agora: apenas se fixa a direção e os seams.

Esta ADR é criada com Status `Proposed` e requer revisão de ao menos outra pessoa antes de passar a `Accepted`.

## Implementation Notes (optional)

- `create_session` e `TrackedSession` evoluem por adição: parâmetro e campo opcionais, sem quebrar chamadas nem serialização (o `StoreRecord`/`from_dict` já é tolerante a campos ausentes).
- A mudança do `registry_id` do warm pool para incluir a versão exige **versionamento/migração do registro** persistido: registros antigos (`warm-pool:{agent}`) precisam ser migrados ou coexistir com os novos (`warm-pool:{agent}:{version}`). Ponto de atenção para o plano do incremento que implementar isto.
- `_filter_ready` passará a considerar a versão ao reconciliar (drain por versão), hoje inexistente.
- A atribuição sticky introduz um novo `type` de registro (por exemplo `version-assignment`), que deve entrar na allowlist de `type` compartilhada (ADR 0001, ADR 0006).
- Escopo sugerido: b2 mantém o comportamento atual; a versão por chamada, `TrackedSession.agent_version`, o seam `VersionSelector` (Latest/Fixed) e o pool por `(agent, version)` entram num incremento posterior; `Weighted`/`Canary` e sticky routing vêm depois.

## References

* docs/architecture/decisions/0002-control-plane-boundary.md (fronteira control-plane; decisão de qualidade fora da Stoke)
* docs/architecture/decisions/0003-warmup-strategies-scheduler.md (warm pool e reconciliação)
* docs/architecture/decisions/0001-durable-store-provider.md (allowlist de `type`, etag, migração de registro)
* python/src/foundry_stoke/warmup/pool.py (registry_id por agente; motivação concreta da colisão)
* python/src/foundry_stoke/session/foundry_adapter.py (resolução de versão no create)
