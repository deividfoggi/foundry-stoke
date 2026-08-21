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

## Notas idiomáticas

- Python: `Protocol`/ABC; métodos `async` para providers com I/O (FileSystem), sync
  aceitável para InMemory. Erros como exceções tipadas ou result.
- .NET: `IDurableStoreProvider`; métodos `...Async` retornando `Task`/`ValueTask` com
  `CancellationToken`; nullable reference types habilitado.
