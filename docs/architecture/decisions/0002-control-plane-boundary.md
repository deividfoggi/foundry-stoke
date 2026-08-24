# control-plane-boundary

**Status**: Proposed
**Date**: 2026-08-21

## Context

A Stoke integra com o Foundry Agent Service. Existe uma escolha de fronteira: a biblioteca pode (a) atuar apenas no control-plane de sessão (criar/referenciar/retomar/consultar o ciclo de vida) ou (b) também embarcar um cliente de data-plane que encapsula os protocolos Responses/Invocations para o tráfego de conversa/negócio da aplicação.

O amendment v1.1 da spec já restringiu o escopo ao control-plane. Este ADR registra a decisão de fronteira e sua justificativa, porque ela é uma decisão arquitetural sistêmica que condiciona todos os outros componentes (warm-up, session control, contratos) e precisa ser explícita e rastreável (FR-016, FR-017a, FR-018).

## Priorities and Requirements (ordered)

1. **Não duplicar o SDK oficial do Foundry** — O tráfego de dados (Responses/Invocations) já é coberto pelo SDK oficial; reimplementá-lo adiciona peso, acoplamento e risco de divergência (FR-018, NFR de footprint leve).
2. **Generalidade para Invocations/containers customizados** — O schema do payload de Invocations é definido pelo container do usuário; um cliente genérico de data-plane não funcionaria para todos os casos.
3. **Foco no valor real da Stoke** — A plataforma explicitamente não provê warm pool nativo; o valor está no control-plane (warm-up, ciclo de vida, estado, store durável), não em repassar tráfego.
4. **Footprint leve** — Evitar dependências e superfície desnecessárias no core (FR-023).
5. **Keepalive ainda precisa de um mínimo de data-plane** — Manter uma sessão viva exige atividade dentro da janela de idle; isso exige um probe mínimo, não um cliente de tráfego completo.

## Options Considered

### Option 1: Control-plane-only + probe de keepalive plugável

A Stoke toca apenas: (1) a API `/sessions` (criar/consultar/parar/encerrar) e (2) um probe mínimo de keepalive (ping genérico Responses opcional embutido + hook fornecido pelo usuário para Invocations/containers customizados). O tráfego de aplicação fica com o SDK oficial do Foundry.

**Evaluation against priorities**:
- **Não duplicar o SDK oficial**: Atende. Nenhum cliente de tráfego de propósito geral é embarcado.
- **Generalidade para Invocations**: Atende. O probe do usuário cobre containers customizados sem a Stoke conhecer o schema.
- **Foco no valor real**: Atende. Concentra-se no warm-up e ciclo de vida, que a plataforma não oferece.
- **Footprint leve**: Atende. Superfície mínima; sem dependência de bibliotecas de protocolo de dados além do necessário para o probe embutido opcional.
- **Keepalive precisa de data-plane mínimo**: Atende. O probe embutido (`responses.create` mínimo) e o hook do usuário resolvem sem virar cliente completo.

### Option 2: Control-plane + cliente de data-plane dual-protocolo embutido

A Stoke embarca um cliente que encapsula Responses e Invocations para o tráfego de aplicação, além do control-plane.

**Evaluation against priorities**:
- **Não duplicar o SDK oficial**: Falha. Reimplementa o que o SDK oficial já faz.
- **Generalidade para Invocations**: Falha. Não há schema genérico de Invocations; o cliente não serviria a todos os containers.
- **Foco no valor real**: Parcial. Dilui o foco e compete com o SDK oficial.
- **Footprint leve**: Falha. Adiciona superfície e dependências significativas.
- **Keepalive precisa de data-plane mínimo**: Atende, mas com custo desproporcional.

## Decision

Adotar a **Option 1**: fronteira **control-plane-only** com probe de keepalive plugável. A Stoke NÃO embarca um cliente de tráfego de propósito geral para Responses/Invocations (FR-017a); a aplicação usa o SDK oficial do Foundry para o data-plane.

Justificativa ancorada nas prioridades: a Option 1 é a única que satisfaz as prioridades 1, 2 e 4. A Option 2 falha em não-duplicação, em generalidade (impossível para Invocations customizados) e em footprint. A prioridade 5 (keepalive precisa de atividade mínima) é resolvida pela abstração de probe, não por um cliente de tráfego, mantendo a fronteira intacta.

Fundamentação nas docs oficiais (ver research.md): idle timeout é medido "após a requisição mais recente"; "o compute segue a sessão"; sessões são provisionadas via criação explícita. Logo, pré-provisionamento é atingível só via control-plane, e keepalive exige um probe mínimo.

## Implementation Notes

- A tabela de fronteiras Control-plane vs Data-plane está em `docs/features/stoke-beta/plan.md`.
- A abstração de probe é detalhada no ADR 0003 e no contrato `warmup-probe.md`.
- **Taxonomia de estado de sessão (confirmada 2026-08-24, spec v1.3)**: o control-plane
  expõe o status oficial `AgentSessionStatus` do Foundry com oito valores (`creating`,
  `active`, `idle`, `updating`, `failed`, `deleting`, `deleted`, `expired`). A Stoke modela
  `SessionState` = esses oito valores mais um `UNKNOWN` de fallback, traduzido
  case-insensitive; qualquer valor desconhecido ou futuro mapeia para `UNKNOWN`, nunca
  coagido para outro status. "Resumed" NÃO é um status: é a transição derivada `idle` ->
  `active` refletida via um marcador `resumed_at`. Fonte:
  https://learn.microsoft.com/en-us/javascript/api/@azure/ai-projects/agentsessionstatus.
- **Gap conhecido/assunção**: não está documentado se uma chamada de control-plane (`GET /sessions/{id}`) reseta o idle timer. Assunção de projeto: não reseta; por isso o keepalive usa um probe de data-plane mínimo. Validar empiricamente antes do GA (registrado como NEEDS RESEARCH em research.md).

## References

* docs/features/stoke-beta/spec.md (FR-016, FR-017, FR-017a, FR-018; Reasoning Log v1.1)
* docs/features/stoke-beta/research.md (fatos de plataforma; superfície de dados fora de escopo)
* docs/architecture/decisions/0003-warmup-strategies-scheduler.md
