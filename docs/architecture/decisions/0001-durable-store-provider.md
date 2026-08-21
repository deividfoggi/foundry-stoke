# durable-store-provider

**Status**: Proposed
**Date**: 2026-08-21

## Context

A Stoke precisa persistir o próprio estado de control-plane (registro de sessões rastreadas, registry de warm-pool, metadados). O beta não deve embarcar nem depender de um banco de produção (Cosmos/Table/Redis são excluídos por escopo), mas o contrato precisa permitir que um provider externo (por exemplo, um Cosmos de terceiros) implemente a persistência sem alterar o core (FR-006, FR-009, FR-011, invariante de não-acoplamento).

O desafio é definir um modelo de registro e uma interface mínima que sejam idiomáticos ao Cosmos (partition key, concorrência otimista por etag) sem introduzir dependência de SDK de store no core, e que sejam implementáveis pelos dois providers de referência do beta (InMemory e FileSystem/JSON).

## Priorities and Requirements (ordered)

1. **Zero acoplamento a store de produção no core** — O core do Stoke MUST NOT importar ou depender de qualquer SDK de store (invariante da spec, CC-004, SC-002). Verificável por inspeção de dependências.
2. **Compatível com Cosmos por design** — O modelo (id + partition key + etag/versão + payload JSON) precisa mapear diretamente para o modelo de item do Cosmos, para que um provider de terceiros seja implementável sem mudar o core (FR-009).
3. **Interface mínima e clara** — CRUD + query-por-partição apenas (FR-008). Sem vazar detalhes de nenhum backend específico. Simplicidade acima de conveniência.
4. **Concorrência otimista garantida** — Toda gravação com etag/versão desatualizado falha por conflito; nunca sobrescreve versão mais recente (FR-007, CC-003, invariante).
5. **Implementável pelos providers de referência do beta** — InMemory e FileSystem/JSON precisam satisfazer o contrato completo, incluindo concorrência otimista e persistência entre reinícios (FR-010).

## Options Considered

### Option 1: Interface de provider agnóstica com modelo Cosmos-friendly (id + partitionKey + etag + payload JSON)

Uma interface pública `DurableStoreProvider` no core, com um record genérico `StoreRecord { id, partitionKey, etag/version, type, payload (JSON) }` e operações mínimas: `create`, `read(id, partitionKey)`, `upsert(record, expectedEtag)`, `delete(id, partitionKey, expectedEtag)`, `queryByPartition(partitionKey)`. Concorrência otimista via etag em upsert/delete. Providers concretos (InMemory, FileSystem/JSON, e futuros externos) vivem fora do core ou como implementações de referência isoladas.

**Evaluation against priorities**:
- **Zero acoplamento**: Atende. O core define apenas a interface e o record; nenhum símbolo de SDK de store aparece.
- **Compatível com Cosmos**: Atende. `id`+`partitionKey`+`etag`+`payload` é o modelo nativo de item do Cosmos; um provider Cosmos implementa a interface 1:1.
- **Interface mínima**: Atende. CRUD + query-por-partição, sem operadores de consulta arbitrária que forçariam uma linguagem de query.
- **Concorrência otimista**: Atende. `expectedEtag` em upsert/delete é o mecanismo; conflito retorna erro tipado.
- **Providers de referência**: Atende. InMemory usa dict + contador de versão; FileSystem serializa JSON com etag no arquivo/registro.

### Option 2: Repositório genérico com abstração de query (LINQ-like / predicados)

Uma interface de repositório mais rica, com suporte a consultas por predicado arbitrário para aproximar a experiência de um ORM.

**Evaluation against priorities**:
- **Zero acoplamento**: Parcial. Uma linguagem de query genérica tende a vazar semântica do backend (o que é traduzível para Cosmos SQL vs. filtro em memória diverge), aumentando o risco de acoplamento implícito.
- **Compatível com Cosmos**: Parcial. Consultas ricas exigiriam tradução por provider; nem todo predicado é eficiente no Cosmos (custo de RU, cross-partition).
- **Interface mínima**: Falha. Introduz complexidade especulativa (FR não pede query arbitrária; só query-por-partição). Viola "clareza acima de abstração".
- **Concorrência otimista**: Atende (ortogonal).
- **Providers de referência**: Parcial. Implementar predicados arbitrários em FileSystem/JSON é trabalhoso e propenso a divergência semântica entre providers.

## Decision

Adotar a **Option 1**: interface `DurableStoreProvider` agnóstica de tecnologia no core, com record Cosmos-friendly (`id` + `partitionKey` + `etag`/versão + `type` discriminador + `payload` JSON) e semântica mínima CRUD + query-por-partição, com concorrência otimista via `expectedEtag`.

Justificativa ancorada nas prioridades: a Option 1 é a única que satisfaz a prioridade 1 (zero acoplamento) e a prioridade 3 (interface mínima) sem comprometer as demais. A Option 2 falha na prioridade 3 ao introduzir complexidade de query não requisitada e ameaça a prioridade 1 por vazamento de semântica de backend. O modelo escolhido mapeia diretamente para o item do Cosmos (prioridade 2), permitindo um provider de terceiros sem alterar o core.

Providers de referência do beta: `InMemory` (dict + versão incremental) e `FileSystem/JSON`. O FileSystem/JSON usa, além da concorrência otimista por etag/versão, **file-locking real cross-process** (advisory lock) para coordenar gravações concorrentes entre processos; ver Implementation Notes.

## Implementation Notes

- Contrato detalhado em `docs/features/stoke-beta/contracts/durable-store-provider.md`; modelo em `docs/features/stoke-beta/data-model.md`.
- **FileSystem/JSON e file-locking**: usar advisory file lock nativo por plataforma (`fcntl.flock` no POSIX / `msvcrt.locking` no Windows para Python; `FileStream` com `FileShare.None`/`FileLock` no .NET). O lock coordena escritores concorrentes no mesmo host; a concorrência otimista por etag continua sendo a fonte de verdade de conflito lógico. Documentar a limitação de portabilidade: advisory locks não são garantidos em sistemas de arquivos de rede (NFS/SMB); o provider FileSystem é para dev local, não produção.
- **Sem dependência de SDK de store no core**: garantir por teste de inspeção de dependências (CC-004) que nenhum pacote de Cosmos/Table/Redis entra no core em nenhuma linguagem.
- Custo de infraestrutura: nenhum no beta (providers InMemory/FileSystem não provisionam recursos Azure). Um provider Cosmos futuro é responsabilidade do terceiro e teria custo próprio.

### Notas de segurança (security-review, 2026-08-21)

- **Sanitização de path no provider FileSystem (SEC-001)**: nunca usar `id`/`partitionKey` crus como segmento de caminho. Derivar o nome de arquivo por hash estável (ex.: SHA-256 hex) ou por um encoding restrito a allowlist do par `id`/`partitionKey`. Confinar todos os arquivos a um diretório base e validar via caminho canônico (`realpath` no POSIX / `Path.GetFullPath` no .NET) que o resultado permanece sob a base; rejeitar chaves vazias ou acima de um limite de tamanho. Bloqueia `../`, separadores de caminho, caminhos absolutos, byte nulo e nomes reservados de Windows (`CON`, `NUL`, `AUX`).
- **Desserialização segura do JSON persistido (SEC-002)**: o JSON relido do disco é entrada não confiável. Usar apenas desserialização por schema — `System.Text.Json` no .NET **sem** `TypeNameHandling`; módulo `json` no Python, **nunca** `pickle`/`eval`. Mapear `payload` por `type` contra uma allowlist explícita de discriminadores conhecidos (`tracked-session`, `warm-pool-registry`) e rejeitar `type` desconhecido. Tratar arquivo corrompido/parcial com erro tipado, sem propagar exceção crua. Impor limite de tamanho de arquivo/payload.
- **Correção do file-lock (SEC-006)**: executar todo o ciclo read-check-etag-write sob o **mesmo** lock, eliminando a janela TOCTOU entre a checagem de etag e a gravação. Definir timeout de aquisição com erro tipado (evita starvation/deadlock). Advisory locks do SO liberam no encerramento do processo (documentar esse comportamento para locks obsoletos após crash); a limitação de NFS/SMB permanece e reafirma o invariante dev-local-only.

## References

* docs/features/stoke-beta/spec.md (FR-006..FR-011, CC-003, CC-004, SC-002, SC-003, invariantes)
* docs/features/stoke-beta/data-model.md
* docs/features/stoke-beta/contracts/durable-store-provider.md
