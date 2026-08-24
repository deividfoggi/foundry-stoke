# Contrato: DurableStoreProvider

- **ADR**: 0001-durable-store-provider
- **Requisitos**: FR-006, FR-007, FR-008, FR-009, FR-010, FR-011; CC-003, CC-004

Interface pública agnóstica de tecnologia para persistir `StoreRecord` (ver data-model.md).
O core não depende de nenhum SDK de store. Semântica: CRUD mínimo + query-por-partição,
com concorrência otimista por `etag`/versão.

## Operações

```
interface DurableStoreProvider:

    create(record: StoreRecord) -> StoreRecord
        # Cria um novo registro. Falha (AlreadyExists) se (id, partitionKey) já existir.
        # Retorna o registro com etag/version inicial atribuído.

    read(id: string, partitionKey: string) -> StoreRecord | NotFound
        # Lê por chave composta. Retorna NotFound (tipado) se ausente.

    upsert(record: StoreRecord, expectedEtag: string | null) -> StoreRecord
        # Cria ou atualiza. Se expectedEtag != null e não casar com o etag atual,
        # falha por ConcurrencyConflict (CC-003). expectedEtag == null só é válido em create.
        # Retorna o registro com novo etag/version.

    delete(id: string, partitionKey: string, expectedEtag: string | null) -> void | NotFound | ConcurrencyConflict
        # Remove por chave composta. Se expectedEtag informado e desatualizado, ConcurrencyConflict.

    queryByPartition(partitionKey: string, typeFilter: string | null) -> list<StoreRecord>
        # Lista registros de uma partição, opcionalmente filtrando por discriminador `type`.
        # NÃO é uma linguagem de query arbitrária (evita acoplamento a backend; ADR 0001).
```

## Erros tipados

| Erro | Quando |
|------|--------|
| `AlreadyExists` | `create` com (id, partitionKey) existente. |
| `NotFound` | `read`/`delete` de chave inexistente. |
| `ConcurrencyConflict` | `upsert`/`delete` com `expectedEtag` desatualizado (CC-003). |

## Providers de referência do beta

- **InMemory**: dict por partição + versão incremental. Para dev/testes. Não persiste
  entre processos.
- **FileSystem/JSON**: serializa registros em JSON no disco; usa **advisory file lock
  cross-process** além da concorrência otimista por etag (ADR 0001, Implementation Notes).
  Portabilidade: advisory locks não são garantidos em NFS/SMB; provider destinado a dev
  local, não produção.

Restrição (invariante): nenhuma implementação no core pode importar SDK de store de
produção (Cosmos/Table/Redis) (CC-004, SC-002). Um provider externo (ex.: Cosmos)
implementa esta mesma interface sem alterar o core (FR-009).

## Responsabilidades do autor de um provider externo

O contrato é intencionalmente minimalista e foi modelado para bancos de documento
(id + partitionKey + etag + JSON), onde partição e concorrência são nativas. Backends
key-value (ex.: Redis) atendem ao contrato, mas o autor do provider assume três
responsabilidades que um document store entrega de graça. A garantia de correção da
Stoke depende do provider honrar estes pontos (ADR 0007).

| Responsabilidade | O que o provider deve garantir |
|------------------|--------------------------------|
| Concorrência otimista (`etag`) | Se o backend não versiona registros nativamente, o provider implementa compare-and-set (ex.: `WATCH`/`MULTI`/`EXEC` ou script Lua no Redis; `If-Match`/`_etag` no Cosmos), guardando a versão junto do registro. `upsert`/`delete` com `expectedEtag` desatualizado devem falhar por `ConcurrencyConflict`. |
| `queryByPartition` | Se o backend não tem query por partição, o provider mantém um índice consistente por partição (ex.: um `SET` de ids por `partitionKey` no Redis), atualizado transacionalmente em `create`/`delete`. Evitar varreduras globais (ex.: `KEYS`/`SCAN` com padrão). |
| Durabilidade e expiração | O store é durável, não cache. O provider (e sua configuração) não pode despejar nem expirar registros da Stoke. Em Redis: habilitar persistência (AOF/RDB), `maxmemory-policy: noeviction` e não aplicar TTL a estes registros. |

Segurança (SEC-005, SEC-008, ADR 0007): a Stoke nunca passa segredos/credenciais ao
provider. A connection string/credencial do backend é construída pela aplicação e o
cliente é injetado no provider, fora da Stoke. A Stoke valida invariantes básicas dos
registros retornados (chaves não vazias, `type` na allowlist), mas não sandboxeia o
provider.

## Notas idiomáticas

- Python: `Protocol`/ABC; métodos `async` para providers com I/O (FileSystem), sync
  aceitável para InMemory. Erros como exceções tipadas ou result.
- .NET: `IDurableStoreProvider`; métodos `...Async` retornando `Task`/`ValueTask` com
  `CancellationToken`; nullable reference types habilitado.
