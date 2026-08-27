# A2 · Servidor MCP en C# para el dominio de reclutamiento

> Plan de **repositorio en construcción**. Proyecto A2 del catálogo en
> [`featured-projects-catalog.md`](https://github.com/Juxn89/juangomezb/blob/feat/projects/docs/plans/featured-projects-catalog.md).
> Versión anterior: En el repo del portfolio bajo `docs/plans/a2-talent-mcp.md`.

## Context

A2 es el primero de los tres repos propios del catálogo: el más corto, el que más diferencia y el que deja
infraestructura de dominio lista para A1 (matching con RAG). La razón de que exista es doble:

1. **Diferenciación real.** Juan ya publicó *"Build an MCP Server in C#"* y *"Consuming MCP Servers from
   .NET"* en dev.to. Un servidor MCP en C# **en producción, contra la revisión vigente del protocolo**, es
   algo que prácticamente nadie tiene en un portfolio, y convierte dos posts en evidencia ejecutable.
2. **Invierte la dependencia con A1.** El catálogo describía A2 como "tools sobre el dominio de A1", pero A1
   no existe y A2 va primero. Se resuelve al revés: **A2 es dueño del dominio** (vacantes, candidatos,
   skills) en PostgreSQL con datos sembrados, y A1 después reutiliza ese mismo esquema y contenedor añadiendo
   pgvector y embeddings. Cero trabajo duplicado.

Aplican las restricciones transversales del catálogo: todo dockerizado, open source sin licencias de pago,
third-party solo si es imprescindible, y **sin demo pública** — la prueba es `docker compose up`, benchmarks
versionados y CI en verde.

---

## Terreno verificado (25 ago 2026)

Esto se comprobó contra las fuentes antes de escribir el plan, porque el protocolo se movió mucho:

| Qué | Estado |
|---|---|
| Revisión del protocolo | **2026-07-28** — la revisión más grande desde el lanzamiento (28 jul 2026) |
| SDK C# | `ModelContextProtocol` **2.2.0 estable** (13 ago 2026), .NET 8/9/10 + netstandard2.0, **Apache-2.0**, 26M descargas |
| Alineación | El SDK v2.x implementa 2026-07-28 con interoperabilidad hacia abajo (2025-11-25 y anteriores) por negociación |
| Paquetes | `.Core` (cliente/servidor low-level) · `ModelContextProtocol` (stdio, hosting/DI, descubrimiento por atributos) · `.AspNetCore` (Streamable HTTP) · `.Extensions.Tasks` · `.Extensions.Apps` (experimental) |

**Cambios de la revisión que condicionan el diseño** (no son detalles — reescriben cómo se construye un
servidor MCP):

- **Sin sesiones.** Se eliminó el header `Mcp-Session-Id` y el handshake `initialize`. El estado entre
  llamadas se lleva con **handles emitidos por el servidor y pasados como argumentos normales de tool**.
  En el SDK, `HttpServerTransportOptions.Stateless` es **`true` por defecto** (poner `false` emite `MCP9006`).
- **`server/discover` es obligatorio** para anunciar versiones soportadas, capacidades e identidad.
- **MRTR** (Multi Round-Trip Requests) reemplaza las peticiones iniciadas por el servidor. El servidor lanza
  `InputRequiredException`; el cliente reintenta la petición original con `inputResponses`.
- **Roots, Sampling y Logging quedaron deprecados** (`MCP9005`). Se registra a `stderr` u **OpenTelemetry**;
  el nivel de log va por petición en `_meta`.
- **Resultados cacheables:** `tools/list`, `prompts/list`, `resources/list`, `resources/read` y
  `resources/templates/list` deben devolver `ttlMs` y `cacheScope`. Y las tools **deberían** venir en orden
  determinista para mejorar el *cache hit* de prompt del LLM.
- **Headers estándar** en POST de Streamable HTTP (`Mcp-Method`, `Mcp-Name`), con promoción de parámetros a
  header vía `[McpHeader]`.
- **Autorización endurecida:** PKCE **S256 obligatorio** (el SDK falla si la metadata del authorization
  server no lo declara), validación del `iss` por RFC 9207, credenciales indexadas por *issuer*, y **DCR
  deprecado** a favor de Client ID Metadata Documents.
- **Trazas:** convención documentada para propagar contexto OpenTelemetry en `_meta`
  (`traceparent`, `tracestate`, `baggage`).
- Otros que rompen: `inputSchema` pasa a ser **obligatorio** al deserializar tools; los retornos que no son
  objeto se emiten crudos en `structuredContent` (`72`, no `{"result": 72}`); Tasks se movió a paquete de
  extensión y no es compatible a nivel de cable con v1.3-1.4.

### Superficie de API confirmada

```csharp
using ModelContextProtocol.Server;
using System.ComponentModel;

[McpServerToolType]
public static class EchoTool
{
    [McpServerTool, Description("Echoes the message back to the client.")]
    public static string Echo(string message) => $"hello {message}";
}
```

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMcpServer()
    .WithHttpTransport()        // stateless por defecto en v2
    .WithToolsFromAssembly();

var app = builder.Build();
app.MapMcp();
```

MRTR en el servidor: `throw new InputRequiredException(inputRequests: {...}, requestState: ...)`, con
`InputRequest.ForElicitation(new ElicitRequestParams {...})`, leyendo la respuesta en
`context.Params.InputResponses` y comprobando `server.IsMrtrSupported`.
Tasks: `.WithTasks(new InMemoryMcpTaskStore())`, extensible vía `IMcpTaskStore`.
Promoción a header: `[McpHeader("Region")] string region`.

---

## Decisiones cerradas (usuario)

| Decisión | Elección |
|---|---|
| Dominio | **Reclutamiento auto-contenido** en Postgres con seeds; A1 lo reutiliza después |
| Artefactos | **Los tres**: `dotnet tool` instalable + imagen Docker en GHCR + librería NuGet reutilizable |
| Auth | **OAuth 2.1 completo con Keycloak** (Apache-2.0) en el compose, scopes por tool |
| Native AOT | **Sí**, con benchmark de cold start publicado; fallback a JIT documentado en ADR si el trimming pelea |

**Consecuencia honesta de alcance:** el catálogo estimaba 1-2 semanas para A2. Con OAuth completo, tres
artefactos publicados, el trabajo de AOT y los lineamientos de arquitectura/E2E, la estimación realista es
**~4-5 semanas**. Sigue siendo el más corto de los tres repos, pero no es un proyecto de fin de semana. Si
hace falta recortar, el orden de corte es: AOT → librería NuGet → Keycloak (a API key). El dominio, las
tools, los tests de arquitectura y el E2E **no se recortan** — son justamente lo que hace que el repo pruebe
algo.

---

## Arquitectura

### Identidad y artefactos

Según la [convención de nombres y tracking](./featured-projects-catalog.md#convención-de-nombres-y-tracking)
del catálogo — repo, slug del portfolio y ruta del case study son la misma cadena:

| | |
|---|---|
| Repo | `juxn89/talent-mcp` |
| Slug / ruta en el portfolio | `talent-mcp` → `/[locale]/projects/talent-mcp` |
| Librería NuGet | `Talent.Mcp.Toolkit` |
| `dotnet tool` | id `Talent.Mcp.Server`, comando `talent-mcp` |
| Imagen | `ghcr.io/juxn89/talent-mcp` |
| Prefijo de proyectos | `Talent.Mcp.*` / `Talent.Domain` |

Comprobado el 25 ago 2026: `Talent.Mcp.*` no existe en NuGet y no hay prefijo reservado que lo bloquee;
tampoco colisiona con los repos actuales de `juxn89`. **Los ids de NuGet son lo único irreversible del
proyecto** — se reconfirman justo antes de la F5, que es cuando se publica.

### GitHub repository metadata

**Description:**
MCP server in C# for the recruitment domain — typed tools for job search, candidate-fit scoring, and skill extraction, built against the 2026-07-28 Model Context Protocol revision. Stateless Streamable HTTP, OAuth 2.1 + PKCE, published as a NuGet package and dotnet tool.

**Topics:**
mcp, model-context-protocol, csharp, dotnet, ai-tooling, oauth2, nuget, hr-tech, clean-architecture, llm-agents, agentic-ai, postgresql, docker, dotnet-tool

---

Aplica el lineamiento 6 del catálogo (**Clean Architecture con la regla de dependencia verificada en CI**).
Una versión anterior de este plan ponía EF Core dentro de `Talent.Domain` — eso violaba la regla y quedó
corregido: la persistencia vive en `Infrastructure` y el dominio no conoce a nadie.

```
/src
  Talent.Domain/             → entidades y reglas puras: scoring, taxonomía de skills.
                               CERO dependencias de framework (ni EF Core, ni SDK MCP, ni ASP.NET)
  Talent.Application/        → casos de uso + puertos: IJobRepository, ICandidateRepository,
                               IHandleCodec, IShortlistScorer. Solo referencia a Domain
  Talent.Infrastructure/     → adaptadores: EF Core/Npgsql, migraciones, seeds, Keycloak, exportadores OTel
  Talent.Mcp.Server/         → presentación: ASP.NET Core, Streamable HTTP → imagen GHCR
  Talent.Mcp.Server.Stdio/   → presentación: host stdio → `dotnet tool`
  Talent.Mcp.Toolkit/        → librería técnica independiente del dominio (primitivas de protocolo) → NuGet
/tests
  Talent.Architecture.Tests/ → regla de dependencia con ArchUnitNET: rompe el PR si Domain toca infraestructura
  Talent.Domain.Tests/       → scoring y normalización deterministas, sin infraestructura ni contenedores
  Talent.Mcp.Tests/          → tools sobre transporte in-memory
  Talent.Mcp.Conformance/    → conformidad de protocolo (discover, MRTR, campos de caché, negociación)
  Talent.Mcp.E2E/            → camino real completo contra el compose: cliente MCP → HTTP → OAuth → Postgres
/bench
  Talent.Mcp.Bench/          → BenchmarkDotNet + medición de cold start
/deploy
  compose.yaml, keycloak/realm.json, otel/collector.yaml, grafana/dashboards/
/docs
  adr/                       → decisiones con trade-offs
```

**El dominio se defiende solo:** el scoring y la normalización de skills son funciones puras sobre
`Talent.Domain`, sin repositorios ni `DbContext` de por medio. Esa es la razón de que sus tests corran en
milisegundos y sin Docker, y de que A1 pueda reutilizarlos tal cual como base de su eval harness.

### Constantes: nada de literales sueltos

Lineamiento 7 del catálogo. Un servidor MCP es terreno fértil para magic strings, así que se fijan desde el
primer commit:

| Constante | Qué encierra |
|---|---|
| `ToolNames` | `search_jobs`, `get_job`, `extract_skills`, … — usados por el servidor, los tests y el cliente demo |
| `McpMetaKeys` | `io.modelcontextprotocol/protocolVersion`, `clientCapabilities`, `logLevel`, `traceparent` |
| `OAuthScopes` | `talent.jobs.read`, `talent.candidates.write`, `talent.candidates.reject` |
| `ProtocolVersions` | la revisión soportada (`2026-07-28`) y las de interoperabilidad hacia abajo |
| `McpErrorCodes` | los del rango reservado `-32020…-32099`, no números crudos en el código |
| `TalentOptions` | TTLs de caché y de handles, tamaño de página, reintentos, timeouts → `IOptions<T>` |

Analizadores Roslyn en `.editorconfig` con severidad de **error**, no de sugerencia.

**`Talent.Mcp.Toolkit` es lo que justifica publicar una librería** — no es un wrapper del SDK, son las
piezas que el SDK no trae y que la revisión nueva vuelve necesarias:

- `PostgresMcpTaskStore : IMcpTaskStore` — el SDK solo trae `InMemoryMcpTaskStore`; sin persistencia, un
  reinicio pierde las tasks en vuelo.
- `HandleCodec` — acuñado, firmado y con TTL, para los *handles* opacos que sustituyen a las sesiones
  (cursores de paginación, shortlists en curso). Firmados para que un cliente no pueda fabricarlos.
- Políticas de `ttlMs` / `cacheScope` por primitiva, y orden determinista de tools.
- Extracción de contexto OTel desde `_meta` (`traceparent`/`tracestate`/`baggage`) a `Activity`.

**Stack en compose** (todo open source, todo gratis): Postgres, Keycloak, el servidor MCP, OTel Collector,
Jaeger, Prometheus y Grafana OSS. Un solo `docker compose up`, seeds incluidos.

### Superficie de tools

| Tool | Qué demuestra |
|---|---|
| `search_jobs` | Paginación con **handle firmado** en lugar de sesión — el patrón que la spec ahora exige |
| `get_job` | Recurso + resultado cacheable con `ttlMs`/`cacheScope` |
| `extract_skills` | Normalización contra taxonomía, **determinista** (sin LLM): testeable y gratis |
| `score_candidate_fit` | Score explicable con desglose por componente (solape de skills, distancia de seniority, ubicación). Determinista → es la base del eval harness de A1 |
| `reject_candidate` | Operación destructiva que exige confirmación vía **MRTR** (`InputRequiredException` + `requestState`) |
| `bulk_score_shortlist` | Larga duración vía **extensión Tasks** con store en Postgres |
| `get_job` con `[McpHeader("Region")]` | Enrutado por región promovido a header — espeja el multi-marca/multi-región de Stepstone |

Nada de esto llama a un LLM, así que el servidor corre sin API keys y sin costo. El LLM entra en A1.

---

## Fases

### F0 · Spike de riesgo y reglas del repo (2 días) — antes de comprometer el diseño
- Verificar el **changelog de 2.0.0 → 2.2.0** (el plan se apoya en las notas de 2.0.0; hay que confirmar qué
  cambió después).
- Probar **Native AOT contra el descubrimiento por atributos**: `WithToolsFromAssembly()` usa reflexión.
  Si el trimming lo rompe, evaluar registro explícito de tools; si tampoco, JIT + ADR con el hallazgo.
- Esqueleto de `compose.yaml` con Postgres y Keycloak arrancando.
- **`AGENTS.md` + `CLAUDE.md` propios del repo** (lineamiento 10). Los del portfolio no aplican aquí: son de
  Next.js/Vercel. `CLAUDE.md` con una línea `@AGENTS.md`, y `AGENTS.md` con un bloque marcado que fije:
  - stack y versiones (**.NET 10**, `ModelContextProtocol` 2.2.0) y la **revisión de MCP soportada**
    (`2026-07-28`), con la lista de lo deprecado que **no se debe usar**: Roots, Sampling, la API de Logging
    de MCP, HTTP+SSE y DCR;
  - la regla de dependencia de Clean Architecture y que `Talent.Architecture.Tests` la hace cumplir;
  - la política de constantes con la tabla de arriba;
  - la pirámide de cinco niveles y que los cinco bloquean el merge;
  - el gate de verificación: nada se cierra sin `dotnet build` + tests + E2E en verde.

  Va en la F0 a propósito: es lo que evita que la siguiente sesión de agente reintroduzca EF Core en el
  dominio o invente un magic string — exactamente el error que este plan ya tuvo una vez.

### F1 · Dominio, capas y toolkit (5-6 días)
- `Talent.Domain`: entidades y reglas puras. El scoring y la normalización de skills como funciones puras,
  con tests de tabla que corren sin Docker.
- `Talent.Application`: puertos (`IJobRepository`, `ICandidateRepository`, `IHandleCodec`) y casos de uso.
- `Talent.Infrastructure`: EF Core/Npgsql, migraciones y seeds realistas — vacantes y perfiles plausibles de
  HR-tech, no lorem ipsum.
- **`Talent.Architecture.Tests` desde este momento, no al final.** Una regla de dependencia que se añade
  cuando el código ya está escrito no se cumple: se negocia. Se escribe antes de que haya nada que romper.
- Las clases de constantes y el `.editorconfig` con los analizadores en severidad de error, también aquí:
  es mucho más barato que extirpar literales después.
- `Talent.Mcp.Toolkit`: `HandleCodec`, políticas de caché, `PostgresMcpTaskStore`.

### F2 · Tools MCP (4-5 días)
- Las 6 tools con `[McpServerToolType]` / `[McpServerTool]`, `inputSchema` explícito en todas.
- `reject_candidate` con MRTR, incluido el camino degradado cuando `server.IsMrtrSupported` es `false`.
- `bulk_score_shortlist` con `.WithTasks(...)` apuntando al store de Postgres.
- Ambos hosts: Streamable HTTP (stateless) y stdio.
- Revisar los retornos: los no-objeto se emiten crudos en `structuredContent`.
- **Primer E2E** (`Talent.Mcp.E2E`) contra el compose, todavía sin auth: cliente MCP real → HTTP → Postgres,
  ejercitando `search_jobs` con paginación por handle y el ciclo MRTR de `reject_candidate`.

### F3 · OAuth 2.1 con Keycloak (3-4 días)
- Realm versionado en `deploy/keycloak/realm.json`, con `code_challenge_methods_supported: ["S256"]`
  declarado — si falta, el OAuth del SDK falla.
- Servidor MCP como resource server, **scopes por tool** (lectura vs escritura vs destructiva).
- Validación de `iss` (RFC 9207) y credenciales indexadas por issuer.
- Usar `ClientOAuthOptions.AuthorizationCallbackHandler` en el cliente demo (el delegate viejo emite
  `MCP9007`).
- ADR sobre Client ID Metadata Documents vs DCR, ahora que DCR está deprecado.
- Extender el E2E al flujo OAuth completo: obtener token contra Keycloak, llamar con y sin el scope
  requerido, y comprobar que la tool destructiva se deniega sin él.

### F4 · Observabilidad (2 días)
- Trazas y métricas OTel, con el contexto extraído de `_meta` para que una traza cruce cliente → servidor.
- **Nada de la API de Logging de MCP** (deprecada): `stderr` en stdio, OTel en HTTP.
- Dashboards de Grafana versionados como código: latencia por tool, tasa de error, tasks en vuelo.

### F5 · Empaquetado y CI (2-3 días)
- `dotnet tool` con instrucciones de configuración para Claude Code y Claude Desktop por stdio.
- Imagen multi-stage, usuario no-root, healthcheck → GHCR.
- Librería a NuGet con SemVer.
- GitHub Actions: en cada PR, **build + arquitectura + unitarios + conformidad + E2E sobre el compose** como
  gate — los cuatro bloquean el merge, no solo informan. Publicar tool, imagen y librería al taggear.

### F6 · AOT, benchmarks y documentación (2-3 días)
- Native AOT (o el hallazgo documentado) con **cold start y memoria antes/después** — relevante de verdad
  porque un servidor stdio se lanza por sesión.
- BenchmarkDotNet sobre el scoring.
- README que abre con `docker compose up`, GIF del flujo, y ADRs enlazados.

---

## Tests

Cinco niveles, cada uno con un trabajo distinto. Los cinco corren en CI y bloquean el merge.

1. **Arquitectura** (`Talent.Architecture.Tests`, ArchUnitNET): `Talent.Domain` no referencia EF Core, el SDK
   de MCP ni ASP.NET; `Talent.Application` solo referencia `Domain`; la presentación no salta a
   `Infrastructure` sin pasar por un puerto. Escrito en F1, antes de que haya código que lo viole.
2. **Unitarios** (xUnit): scoring y normalización de skills como funciones puras, con casos de tabla. Sin
   Docker, milisegundos.
3. **Tools** sobre transporte in-memory: contrato de entrada/salida, `inputSchema` presente, errores
   accionables, y que los handles ajenos o expirados se rechacen.
4. **Conformidad de protocolo** — el suite que más señal da: `server/discover` responde versiones y
   capacidades; el ciclo MRTR completo (primer `input_required` → reintento con `inputResponses`);
   `ttlMs`/`cacheScope` presentes en todas las listas; orden de tools estable entre llamadas; negociación
   hacia abajo con un cliente 2025-11-25; y que Keycloak declare `S256` en su metadata.
5. **E2E** (`Talent.Mcp.E2E`) — lineamiento 8, **sin mocks**: un cliente MCP real contra el stack de
   `docker compose` (Postgres + Keycloak + servidor), atravesando OAuth. Cubre los caminos que solo fallan
   al integrar: paginación por handle entre llamadas, MRTR completo, denegación por scope, y una task que
   sobrevive al reinicio del contenedor. Testcontainers (MIT) levanta las dependencias en CI.

---

## Riesgos

| Riesgo | Mitigación |
|---|---|
| Native AOT vs descubrimiento por reflexión | F0 lo decide antes de construir sobre esa base; fallback a registro explícito y luego a JIT, con ADR |
| El plan se apoya en notas de 2.0.0, no de 2.2.0 | Primera tarea de F0: leer el changelog 2.0.0→2.2.0 |
| Keycloak sin `S256` en su metadata | Realm versionado y un test de conformidad que lo asserta |
| La spec se sigue moviendo rápido | Fijar la revisión en el README y en un test; la política de deprecación da 12 meses de ventana |
| Alcance real de ~4 semanas | Orden de corte explícito: AOT → librería → Keycloak a API key |

---

## Verificación

```bash
docker compose up -d                            # Postgres, Keycloak, OTel, Jaeger, Prometheus, Grafana, servidor
dotnet test tests/Talent.Architecture.Tests     # regla de dependencia (no necesita el compose)
dotnet test tests/Talent.Domain.Tests           # dominio puro, sin Docker
dotnet test                                     # todo: + tools, conformidad y E2E
dotnet run --project bench                      # BenchmarkDotNet + cold start
```

Comprobaciones manuales:
1. `dotnet tool install -g` + configurar en Claude Code por stdio → las 6 tools aparecen y `search_jobs`
   devuelve datos sembrados.
2. `reject_candidate` sin razón → llega `input_required`; el reintento con `inputResponses` la ejecuta.
3. `bulk_score_shortlist`, reiniciar el contenedor, y la task sigue consultable (el store de Postgres
   justifica su existencia).
4. Una traza en Jaeger que cruza cliente demo → servidor → Postgres en un solo árbol.
5. Llamada sin token → 401; token sin el scope de la tool destructiva → denegado.
6. `tools/list` dos veces → mismo orden y `ttlMs`/`cacheScope` presentes.

---

## Fuentes

- [The 2026-07-28 Specification](https://blog.modelcontextprotocol.io/posts/2026-07-28/)
- [Key Changes — 2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28/changelog)
- [modelcontextprotocol/csharp-sdk](https://github.com/modelcontextprotocol/csharp-sdk)
- [NuGet: ModelContextProtocol 2.2.0](https://www.nuget.org/packages/ModelContextProtocol)
- [Announcing v2.0 of the official MCP C# SDK — .NET Blog](https://devblogs.microsoft.com/dotnet/announcing-v20-of-the-official-mcp-csharp-sdk/)
- [Release v2.0.0 — csharp-sdk](https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.0.0)
- [MCP C# SDK 2.0: Stateless HTTP, Interactive Tools and a Practical Migration Path](https://benjamin-abt.com/blog/2026/08/03/mcp-csharp-sdk-2/)
