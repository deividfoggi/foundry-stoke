# Contrato: SessionController

- **ADR**: 0002-control-plane-boundary, 0005-authentication-strategy
- **Requisitos**: FR-001..FR-005, FR-016; CC-001, CC-002, SC-005

Abstração de control-plane do ciclo de vida de sessão. Encapsula as operações oficiais de
`/sessions` de forma agnóstica de protocolo (sem tráfego de dados de aplicação; ADR 0002).
Python usa o session control tipado do `azure-ai-projects`; .NET usa REST fallback contra a
API `/sessions` oficial via pipeline `Azure.Core`/`AIProjectClient` (ADR 0005), atrás desta
mesma interface.

## Operações

```
interface SessionController:

    createSession(agentDefinitionId: string, idleTimeoutSeconds: int = 900) -> TrackedSession
        # Cria/pré-provisiona uma sessão. Retorna agent_session_id e estado inicial (Active).
        # Valida idleTimeoutSeconds no range 300..3600; fora dele -> InvalidIdleTimeout (CC-002).

    getSession(agentDefinitionId: string, agentSessionId: string) -> TrackedSession | NotFound
        # Consulta o estado atual (mapeado para Active/Idle/Resumed via tradução em runtime).

    listSessions(agentDefinitionId: string) -> list<TrackedSession>
        # Lista sessões conhecidas para a definição de agente.

    stopSession(agentDefinitionId: string, agentSessionId: string) -> void
        # Para o compute preservando o volume persistente (pode ser retomada). Idempotente.

    deleteSession(agentDefinitionId: string, agentSessionId: string) -> void
        # Libera os recursos. Operações subsequentes sobre a sessão encerrada -> SessionClosed (FR-005).
```

Notas semânticas (fundamentadas em research.md):

- `Resumed` NÃO é operação explícita: é o efeito de referenciar novamente uma sessão Idle.
  Não há endpoint "resume"; `getSession`/uso reativa. `SessionController` reflete a
  transição, não a força por um método dedicado.
- Idle timeout é definido na criação da versão do agente (imutável na versão); o beta valida
  o range na entrada e propaga a configuração.

## Erros tipados

| Erro | Quando |
|------|--------|
| `InvalidIdleTimeout` | `createSession` com valor fora de 300..3600 (CC-002). |
| `NotFound` | `getSession` de sessão inexistente. |
| `SessionClosed` | operação sobre sessão já encerrada (FR-005, determinístico). |
| `FoundryUnavailable` | serviço indisponível/timeout; propagado tipado, sem retry avançado no beta. |

## Gaps conhecidos / assunções

- Operações de sessão tipadas no SDK .NET não confirmadas: assumir REST fallback via
  `Azure.Core` até confirmação (ADR 0005; research.md, NEEDS RESEARCH).
- Strings exatas do enum de status oficial não documentadas: enum próprio + tradução em
  runtime (research.md).

## Notas idiomáticas

- Python: `async def`, com variantes sync onde fizer sentido; `agent_session_id`.
- .NET: `ISessionController` com `...Async`, `Task<TrackedSession>`, `CancellationToken`.
