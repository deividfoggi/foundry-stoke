# cross-language-api-consistency

**Status**: Proposed
**Date**: 2026-08-21

## Context

A Stoke é um SDK multilinguagem cuja superfície pública precisa ser semanticamente equivalente entre linguagens, mas idiomática em cada uma (FR-021, FR-022). Três decisões acopladas precisam ser registradas: (1) como garantir e verificar a equivalência semântica (mecanismo de conformidade), (2) como versionar/releasar as linguagens, e (3) o escopo de linguagens do beta.

A pesquisa confirmou que **Go não possui SDK oficial do Foundry** (apenas a REST `/sessions`), enquanto Python (`azure-ai-projects`) e .NET (`Azure.AI.Projects`) possuem. Isso força uma decisão de escopo de beta.

## Priorities and Requirements (ordered)

1. **Equivalência semântica verificável** — A superfície pública precisa ser equivalente entre linguagens, validável por uma suíte de conformidade compartilhada (FR-022, SC-001, CC-001..CC-007).
2. **Não inventar APIs** — Só encapsular operações oficiais confirmadas; onde não há SDK oficial, não fabricar superfície (FR-018). Go sem SDK oficial não pode ter implementação de control-plane baseada em SDK.
3. **Idiomático por linguagem** — Python async (sync onde fizer sentido), .NET Task-based; cada um respeita suas convenções (FR-021).
4. **Release sustentável** — O modelo de versionamento não deve acoplar linguagens artificialmente nem bloquear correções pontuais de uma linguagem.
5. **Extensibilidade para Go sem quebra** — O contrato deve permitir adicionar Go depois sem mudança quebrando Python/.NET.

## Options Considered

### Option 1 (conformidade): Fixtures de cenário agnósticas de linguagem executadas por harness fino por linguagem

Cenários descritos em fixtures declarativas (YAML/JSON) como fonte única de verdade, executados por um harness fino por linguagem. Foco em equivalência comportamental/semântica, não em snapshots de output.

**Evaluation against priorities**:
- **Equivalência verificável**: Atende. Uma fonte única de cenários evita divergência entre suítes por linguagem.
- **Não inventar APIs**: Atende (ortogonal).
- **Idiomático**: Atende. O harness fino adapta a execução ao idioma; o comportamento é o contrato.
- **Release sustentável**: Atende (ortogonal).
- **Extensibilidade Go**: Atende. Adicionar Go = adicionar um harness que roda as mesmas fixtures.

### Option 2 (conformidade): Golden snapshots de output por linguagem

Cada linguagem gera outputs comparados a arquivos golden.

**Evaluation against priorities**:
- **Equivalência verificável**: Parcial. Snapshots capturam forma de output, não equivalência semântica; diferenças idiomáticas legítimas geram falso-positivo.
- **Idiomático**: Falha. Força output idêntico, conflitando com idiomática por linguagem.
- **Extensibilidade Go**: Parcial. Novos goldens por linguagem, com risco de drift.

### Versionamento — Option A: semver independente por linguagem (PyPI e NuGet versionados independentemente)

**Evaluation**: Atende à prioridade 4. Uma correção específica de Python não força bump no NuGet. A equivalência semântica é garantida pela suíte de conformidade, não por número de versão compartilhado.

### Versionamento — Option B: lockstep (mesma versão em todas as linguagens)

**Evaluation**: Falha na prioridade 4. Acopla releases artificialmente; um hotfix de uma linguagem força republicar a outra sem mudança real.

## Decision

- **Conformidade**: adotar **Option 1** — fixtures de cenário agnósticas de linguagem (YAML/JSON) como fonte única de verdade, executadas por um harness fino por linguagem, focando em equivalência comportamental/semântica (não snapshots).
- **Versionamento/release**: adotar **semver INDEPENDENTE por linguagem** (PyPI e NuGet versionados independentemente). A equivalência é assegurada pela suíte de conformidade; mudanças de comportamento que afetam a superfície pública devem ser refletidas em todas as linguagens implementadas antes do release, ou a divergência registrada em ADR.
- **Escopo de linguagens do beta**: **Python + .NET apenas**. **Go é adiado** até existir um SDK oficial do Foundry para Go (pesquisa confirmou que Go tem somente a REST `/sessions`, sem SDK oficial). Isso satisfaz a prioridade 2 (não inventar APIs). O contrato cross-language permanece projetado para admitir Go depois sem quebra (prioridade 5): as interfaces (`SessionController`, `DurableStoreProvider`, `WarmupStrategy`, `WarmupProbe`, `CredentialProvider`, `Clock`/`Scheduler`) são independentes de linguagem e um harness Go pode ser adicionado às mesmas fixtures.

Justificativa: a Option 1 de conformidade satisfaz as prioridades 1 e 3 sem o falso-positivo dos goldens (Option 2). O versionamento independente satisfaz a prioridade 4. O escopo Python+.NET satisfaz a prioridade 2, com extensibilidade preservada (prioridade 5).

## Implementation Notes

- Fixtures compartilhadas vivem uma vez no repositório; cada harness (Python, .NET) as consome. Local sugerido: pasta de conformidade cross-language sob o monorepo (definir no decompose).
- **Go deferral**: `go/` fica reservado nas convenções do repositório; a coding-guidelines foi atualizada para refletir beta = Python + .NET.
- Release: pipelines de publicação PyPI e NuGet são independentes; cada uma com sua tag/semver.
- **Gap conhecido**: enums de status de sessão não têm strings documentadas; a Stoke expõe um enum próprio e traduz em runtime (ver research.md). A suíte de conformidade valida a semântica do estado, não a string bruta.

## References

* docs/features/stoke-beta/spec.md (FR-021, FR-022, SC-001, User Story 5; Spec Evolution Log v1.2)
* docs/features/stoke-beta/research.md (Go sem SDK oficial; enum de status não documentado)
* .github/docs/coding-guidelines.md (Project-Specific Conventions)
