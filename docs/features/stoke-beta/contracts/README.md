# Contratos Cross-Language: Stoke Beta

- **Criado em**: 2026-08-21
- **Spec base**: docs/features/stoke-beta/spec.md (v1.2)

Estes contratos são **agnósticos de linguagem**. Descrevem a semântica pública que deve
ser equivalente entre Python e .NET no beta (Go adiado; ADR 0004), com implementação
idiomática por linguagem. Assinaturas usam pseudo-notação; cada linguagem adapta nomes e
tipos às suas convenções (FR-021, FR-022).

Convenções de idioma:

| Conceito | Python (`foundry_stoke`) | .NET (`Foundry.Stoke`) |
|----------|--------------------------|-------------------------|
| Assíncrono | `async def` (+ sync onde fizer sentido) | `Task`/`ValueTask`, sufixo `Async` |
| Interface | `Protocol`/ABC | `interface` (prefixo `I`) |
| Erro esperado | resultado/exceção tipada | exceção tipada / result |
| Cancelamento | `asyncio` / timeouts | `CancellationToken` |

Contratos:

- [durable-store-provider.md](durable-store-provider.md) — persistência (ADR 0001)
- [session-controller.md](session-controller.md) — ciclo de vida de sessão (ADR 0002, 0005)
- [warmup-strategy.md](warmup-strategy.md) — estratégias de aquecimento (ADR 0003)
- [warmup-probe.md](warmup-probe.md) — probe plugável (ADR 0003)
- [credential-provider.md](credential-provider.md) — autenticação (ADR 0005)
- [clock-scheduler.md](clock-scheduler.md) — clock/scheduler não bloqueante (ADR 0003)

Regra de erro comum: falhas esperadas (conflito de concorrência, sessão encerrada,
credencial ausente, idle timeout inválido) são reportadas por erro tipado determinístico,
nunca por exceção genérica. Erros do serviço Foundry são propagados tipados, sem retry
avançado no beta.
