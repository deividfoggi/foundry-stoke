# Contrato: WarmupProbe

- **ADR**: 0003-warmup-strategies-scheduler, 0002-control-plane-boundary
- **Requisitos**: FR-017; CC-007, SC-005

Abstração de probe de keepalive. Gera atividade mínima dentro da janela de idle para manter
uma sessão utilizável, sem a Stoke embarcar um cliente de tráfego de propósito geral
(ADR 0002, FR-017a). Duas fontes: ping genérico Responses embutido (opcional) e hook
fornecido pelo usuário para Invocations/containers customizados.

## Operação

```
interface WarmupProbe:

    probe(agentDefinitionId: string, agentSessionId: string) -> ProbeResult
        # Executa uma atividade mínima contra a sessão para resetar/renovar o idle timer.
        # ProbeResult: { ok: bool, latency: duração, error?: tipado }
```

## Implementações

### ResponsesPingProbe (embutida, opcional)

- Envia um `responses.create` mínimo com `agent_session_id`, usando o cliente OpenAI do
  Foundry (`get_openai_client(...)` no Python). Conta como atividade de data-plane mínima.
- Aplicável apenas a agentes compatíveis com Responses. Para Invocations/containers
  customizados, exige probe do usuário (edge case da spec; CC-007).

### UserProbe (hook fornecido pelo usuário)

- Callback/delegate fornecido pela aplicação. A Stoke o invoca dentro da janela de idle.
- Usado para Invocations/containers customizados, cujo schema é definido pelo usuário; a
  Stoke não conhece o payload (ADR 0002, prioridade de generalidade).
- A Stoke não envia tráfego de aplicação por conta própria neste caso (CC-007, invariante
  de fronteira).

## Notas idiomáticas

- Python: `WarmupProbe` como `Protocol`; `ResponsesPingProbe` embutida; user probe = coroutine/callable.
- .NET: `IWarmupProbe`; user probe = `Func<..., Task<ProbeResult>>` (delegate); `...Async`.

## Notas de segurança (SEC-010, ADR 0007)

- O endpoint alvo do probe vem **exclusivamente de configuração de confiança** (env/vault,
  ex.: `FOUNDRY_PROJECT_ENDPOINT`), nunca de entrada não confiável. É validado antes do uso:
  esquema `https` e host esperado. Isso limita a superfície de SSRF (evita redirecionar o
  ping, com token anexado, para um endpoint arbitrário).
- O probe do usuário é código da aplicação (confiado, in-process; ADR 0007). A Stoke **não
  anexa credenciais** ao invocá-lo e passa apenas `agentDefinitionId`/`agentSessionId`.

