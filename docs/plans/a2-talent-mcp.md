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

## Estado (16 sep 2026)

**Las seis fases están cerradas.** F0 arrancó el 27 ago 2026 y F6 cerró el 10 sep; el trabajo
posterior a F6 está listado al final de esta sección.

| Fase | Fechas | |
|---|---|---|
| F0 · Spike y reglas | 27 ago | ✅ |
| F1 · Dominio, capas y toolkit | 28 ago | ✅ |
| F2 · Tools MCP | 1-2 sep | ✅ |
| F3 · OAuth 2.1 con Keycloak | 3 sep | ✅ |
| F4 · Observabilidad | 4 sep | ✅ |
| F5 · Empaquetado y CI | 7 sep | ✅ |
| F6 · AOT, benchmarks y documentación | 8-10 sep | ✅ |

**Duración real: ~2 semanas**, no las 4-5 que estimaba la sección de alcance más abajo. Esa
estimación se deja como estaba escrita, porque una previsión corregida a posteriori deja de ser una
previsión.

Publicado: `Talent.Mcp.Toolkit` y `Talent.Mcp.Server` en NuGet — `1.1.0` el 16 sep 2026, con
README de paquete, símbolos y SourceLink — más la imagen en GHCR. Siete ADRs, cuatro registros de
verificación, 269 métodos de test repartidos en seis proyectos.

> Las versiones `1.0.2` a `1.0.5` existen como tags y **no publicaron nada**: `dotnet pack` tomaba
> la versión del csproj en vez del tag, NuGet respondía `409` y `--skip-duplicate` lo daba por
> éxito. En nuget.org solo están `1.0.1`, `1.0.6` y `1.1.0`.

### Lo que el plan pedía y no salió como estaba escrito

- **Native AOT.** No se consiguió, y la razón está medida en
  [`ADR-0007`](../adr/0007-trim-clean-over-native-aot.md): la build recortada no arranca. Ver la
  tabla de decisiones más abajo, corregida.
- **El GIF del flujo** de F6 se sustituyó por un transcript de terminal anotado — reproducible, y no
  se desactualiza en silencio como un GIF.
- **La "negociación hacia abajo"** de la sección de tests no existe bajo `SessionMode.Stateless`; ver
  [`ADR-0001`](../adr/0001-streamable-http-session-mode.md).

### Trabajo posterior a F6

Ninguno era una fase; todo salió de revisar lo que realmente se publicaba.

| | |
|---|---|
| Seeding al arranque | `docker compose up` no sembraba el dominio: el único `MigrateAsync` vivía en `TalentSeeder`, llamado solo desde fixtures. El principio 1 del catálogo promete lo contrario |
| `HandleCodec` trim-safe | Sobrecargas con `JsonTypeInfo`; las viejas obsoletas y anotadas. No rompe API |
| Calidad de paquete | El `.nupkg` llevaba solo la DLL: sin README (página en blanco en NuGet), sin símbolos, sin SourceLink |
| Gate de API | Valida cada pack contra la última versión publicada |
| Config de desarrollo fuera de los artefactos | `appsettings.Development.json` viajaba dentro del paquete y de la imagen, con la clave de firma de handles |
| Versión derivada del tag | v1.0.2–v1.0.5 se publicaron estampadas `1.0.1` y NuGet las descartó en silencio |
| Trigger de CI | Filtraba por prefijo de rama; las que no estaban listadas no ejecutaban nada y no lo decían |
| Test de reinicio | La comprobación 3 de más abajo, que era la única sin cubrir |

Todo lo anterior se publicó como **`v1.1.0` el 16 sep 2026**. Minor: añade API —las sobrecargas de
`HandleCodec` con `JsonTypeInfo`, `MigrateAndSeedAsync`— y no quita ninguna, lo que el gate de
validación de paquete comprueba contra la línea base publicada en cada `pack` en vez de fiarlo a
quien escribe el número de versión.

### Abierto, y ninguno bloquea nada

| | |
|---|---|
| Imágenes GHCR sin verificar | Los jobs salieron en verde; nadie ha hecho `docker pull` contra el registro |
| Comprobación 1 del plan | CI instala el tool y verifica las seis tools; configurarlo en un cliente MCP real no lo ha hecho nadie |
| Paquetes por RID para ReadyToRun | 324 → 177 ms de arranque, a cambio de perder la instalación multiplataforma de un solo paquete. Decisión de distribución, no de rendimiento; cifras en [`ADR-0007`](../adr/0007-trim-clean-over-native-aot.md) |
| 24 `await using` en el task store | Sus `DisposeAsync` implícitos capturan el contexto. Supresión acotada y fechada en `.editorconfig`. Exposición práctica nula: quien consume un `IMcpTaskStore` son servidores MCP, que son consola o ASP.NET Core y no tienen `SynchronizationContext` |
| Trimming | Un bloqueante localizado, y **sin consumidor**: nada de lo que se publica va recortado |

---

## Terreno verificado (25 ago 2026)

> **Nota (27 ago 2026, F0):** esta sección se escribió contra las notas de release de **2.0.0**. La
> tarea de F0 "verificar el changelog 2.0.0 → 2.2.0" ya se ejecutó y corrigió dos afirmaciones de
> aquí — ver [`docs/verification/sdk-2.0.0-to-2.2.0-review.md`](../verification/sdk-2.0.0-to-2.2.0-review.md)
> y [`ADR-0001`](../adr/0001-streamable-http-session-mode.md). En concreto: `Stateless` **no** es
> obsoleto y ponerlo en `false` no emite `MCP9006` (ese diagnóstico lo llevan cinco propiedades de
> sesión), y SDK 2.2.0 introdujo `HttpServerSessionMode`, que sustituye al booleano. El resto del
> plan sigue en pie: no hubo cambios rompientes en 2.1.0 ni 2.2.0.

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

> **Nota (27 ago 2026, F0):** los dos fragmentos de abajo son los de la documentación del SDK y **no
> son la forma que usa este repo**. El spike de AOT ([`ADR-0002`](../adr/0002-native-aot-and-explicit-tool-registration.md))
> midió que el trimming vacía silenciosamente las tools descubiertas por reflexión: `tools/list`
> responde `-32601` sin crash ni log de error. Se registra con `WithTools<T>()` explícito, y las
> clases de tool **no pueden ser `static`** (`WithTools<T>` las rechaza con `CS0718`).

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
| ~~Native AOT~~ | **Corregido por medición (10 sep 2026).** La build recortada no arranca: el SDK construye el schema de cada tool reflejando sobre sus tipos de parámetro, y un enum del dominio no tiene `JsonTypeInfo` una vez recortado. ReadyToRun parte el arranque por la mitad (324 → 177 ms) pero no es aplicable al artefacto que lo necesita: el host stdio se distribuye como IL portable. Se envía **trim-clean con el hallazgo documentado**, que es lo que el fallback de esta misma fila preveía. [`ADR-0007`](../adr/0007-trim-clean-over-native-aot.md) |

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
  Talent.Mcp.Tools/          → las seis tools, sus contratos de wire y el registro compartido.
                               NO referencia Infrastructure ni ModelContextProtocol.AspNetCore: ambos
                               hosts la cargan y uno es el stdio. Añadido en F2, ver ADR-0004
  Talent.Mcp.Server/         → presentación: ASP.NET Core, Streamable HTTP → imagen GHCR
  Talent.Mcp.Server.Stdio/   → presentación: host stdio → `dotnet tool`
  Talent.Mcp.Toolkit/        → librería técnica independiente del dominio (primitivas de protocolo) → NuGet
/tests
  Talent.Architecture.Tests/ → regla de dependencia con ArchUnitNET: rompe el PR si Domain toca infraestructura
  Talent.Domain.Tests/       → scoring y normalización deterministas, sin infraestructura ni contenedores
  Talent.Infrastructure.Tests/ → mapeo EF y repositorios contra Postgres real (Testcontainers). Un SEXTO
                               proyecto que este plan no preveía: no había sitio para "¿el mapeo funciona
                               de verdad contra Postgres?", y esa respuesta solo existe en el proveedor
                               real (text[], ILIKE, containment, aplanado de owned types)
  Talent.Mcp.Tests/          → tools sobre transporte in-memory
  Talent.Mcp.Conformance/    → conformidad de protocolo (discover, MRTR, campos de caché, negociación)
  Talent.Mcp.E2E/            → camino real completo contra el compose: cliente MCP → HTTP → OAuth → Postgres
/bench
  Talent.Mcp.Bench/          → BenchmarkDotNet sobre las funciones puras del dominio
/scripts                     → verify-f5, verify-f6 y la medición de cold start que corre en CI
/deploy
  compose.yaml, keycloak/realm.json, otel/collector.yaml, grafana/, loki/, prometheus/, postgres/
/docs
  adr/                       → decisiones con trade-offs (siete a 16 sep 2026)
  verification/              → registros fechados: qué se comprobó, contra qué fuente, cuándo
  plans/                     → este fichero
  INSTALLATION.md, DEVELOPMENT.md, CLAUDE_INTEGRATION.md
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
| `OAuthScopes` | `talent.jobs.read`, `talent.candidates.read`, `talent.candidates.write`, `talent.candidates.reject` — cuatro, no tres: leer candidatos se separó de escribirlos en F3 |
| `ProtocolVersions` | la revisión soportada (`2026-07-28`) y las de interoperabilidad hacia abajo |
| ~~`McpErrorCodes`~~ | **Descartada, verificado 1 sep 2026.** La banda no está libre — el propio `McpErrorCode` del SDK ya define `-32020`, `-32021`, `-32022` y `-32042` — y `McpException` en 2.2.0 no expone miembro de código, así que una tool no puede emitir uno propio. Los fallos van en el resultado: `McpException` con un mensaje accionable e `isError: true` |
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

### F0 · Spike de riesgo y reglas del repo ✅ 27 ago 2026
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

### F1 · Dominio, capas y toolkit ✅ 28 ago 2026
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

### F2 · Tools MCP ✅ 1-2 sep 2026
- Las 6 tools con `[McpServerToolType]` / `[McpServerTool]`, `inputSchema` explícito en todas.
- `reject_candidate` con MRTR, incluido el camino degradado cuando `server.IsMrtrSupported` es `false`.
- `bulk_score_shortlist` con `.WithTasks(...)` apuntando al store de Postgres.
- Ambos hosts: Streamable HTTP (stateless) y stdio.
- Revisar los retornos: los no-objeto se emiten crudos en `structuredContent`.
- **Primer E2E** (`Talent.Mcp.E2E`) contra el compose, todavía sin auth: cliente MCP real → HTTP → Postgres,
  ejercitando `search_jobs` con paginación por handle y el ciclo MRTR de `reject_candidate`.

### F3 · OAuth 2.1 con Keycloak ✅ 3 sep 2026
- Realm versionado en `deploy/keycloak/realm.json`, con `code_challenge_methods_supported: ["S256"]`
  declarado — si falta, el OAuth del SDK falla.
- Servidor MCP como resource server, **scopes por tool** (lectura vs escritura vs destructiva).
- Validación de `iss` (RFC 9207) — hecho, ejercitado por el flujo real authorization_code + PKCE en
  `AuthorizationCodeE2ETests`, que captura el `iss` de la redirección y confía en que el SDK lo valide.
  **"Credenciales indexadas por issuer" queda fuera de alcance, no pendiente**: ese requisito del spec
  (sección "Authorization Server Binding") es para un cliente que persiste credenciales OAuth entre
  ejecuciones y no debe reutilizarlas si cambia el authorization server. Este proyecto no publica un
  cliente así — la tabla de "Published artifacts" de AGENTS.md no incluye uno, y `Talent.Mcp.E2E` (el
  único "cliente" que existe) mintea un token por test y no persiste nada. No hay artefacto al que
  aplicarle el requisito. Detalle en `deploy/keycloak/README.md`.
- Usar `ClientOAuthOptions.AuthorizationCallbackHandler` en el cliente demo (el delegate viejo emite
  `MCP9007`).
- **ADR-0005, resuelto distinto de lo asumido.** DCR sigue deprecado, pero CIMD tampoco se adopta: el
  soporte de Keycloak es experimental desde 26.6.0 (abril 2026), detrás de `--features=cimd`, con un
  bug abierto (keycloak/keycloak#49730) que lo deja inutilizable para clientes tipo MCP. La decisión es
  **pre-registro** (clientes estáticos en el realm, ya presentes desde F0) — que además es la opción
  que el propio spec prioriza primero cuando cliente y servidor ya tienen una relación previa. Revisar
  cuando Keycloak promueva CIMD a preview (roadmap: 26.8.0, fin de sept. 2026) o el bug se cierre.
- Extender el E2E al flujo OAuth completo: obtener token contra Keycloak, llamar con y sin el scope
  requerido, y comprobar que la tool destructiva se deniega sin él.

### F4 · Observabilidad ✅ 4 sep 2026
- Trazas y métricas OTel, con el contexto extraído de `_meta` para que una traza cruce cliente → servidor.
- **Nada de la API de Logging de MCP** (deprecada): `stderr` en stdio, OTel en HTTP.
- Dashboards de Grafana versionados como código: latencia por tool, tasa de error, tasks en vuelo.

### F5 · Empaquetado y CI ✅ 7 sep 2026
- `dotnet tool` con instrucciones de configuración para Claude Code y Claude Desktop por stdio.
- Imagen multi-stage, usuario no-root, healthcheck → GHCR.
- Librería a NuGet con SemVer.
- GitHub Actions: en cada PR, **build + arquitectura + unitarios + conformidad + E2E sobre el compose** como
  gate — los cuatro bloquean el merge, no solo informan. Publicar tool, imagen y librería al taggear.

### F6 · AOT, benchmarks y documentación ✅ 8-10 sep 2026
- Native AOT (o el hallazgo documentado) con **cold start y memoria antes/después** — relevante de verdad
  porque un servidor stdio se lanza por sesión.
- BenchmarkDotNet sobre el scoring.
- README que abre con `docker compose up`, ~~GIF del flujo~~ **transcript de terminal anotado**, y ADRs enlazados. El GIF se sustituyó por decisión explícita: es reproducible, copiable, y no se desactualiza en silencio cuando cambia la superficie.

---

## Tests

Cinco niveles planificados, **seis proyectos en realidad** — `Talent.Infrastructure.Tests` se añadió en F1 porque el mapeo EF contra Postgres real no tenía sitio en esta lista. Todos corren en CI y todos bloquean el merge.

1. **Arquitectura** (`Talent.Architecture.Tests`, ArchUnitNET): `Talent.Domain` no referencia EF Core, el SDK
   de MCP ni ASP.NET; `Talent.Application` solo referencia `Domain`; la presentación no salta a
   `Infrastructure` sin pasar por un puerto. Escrito en F1, antes de que haya código que lo viole.
2. **Unitarios** (xUnit): scoring y normalización de skills como funciones puras, con casos de tabla. Sin
   Docker, milisegundos.
3. **Tools** sobre transporte in-memory: contrato de entrada/salida, `inputSchema` presente, errores
   accionables, y que los handles ajenos o expirados se rechacen.
4. **Conformidad de protocolo** — el suite que más señal da: `server/discover` responde versiones y
   capacidades; el ciclo MRTR completo (primer `input_required` → reintento con `inputResponses`);
   `ttlMs`/`cacheScope` presentes en todas las listas; orden de tools estable entre llamadas; y que
   Keycloak declare `S256` en su metadata.
   > **Corregido en F2.** Esta línea pedía "negociación hacia abajo con un cliente 2025-11-25". Bajo
   > `SessionMode.Stateless` no hay downgrade que negociar: ese cliente se sirve statelessly, sin
   > `Mcp-Session-Id` acuñado ni devuelto, y GET/DELETE responden `405`. Aseverar `-32022` sería aseverar
   > un comportamiento `Stateful` que este servidor deliberadamente no tiene.
   > [`ADR-0001`](../adr/0001-streamable-http-session-mode.md)
5. **E2E** (`Talent.Mcp.E2E`) — lineamiento 8, **sin mocks**: un cliente MCP real contra el stack de
   `docker compose` (Postgres + Keycloak + servidor), atravesando OAuth. Cubre los caminos que solo fallan
   al integrar: paginación por handle entre llamadas, MRTR completo y denegación por scope.
   Testcontainers (MIT) levanta las dependencias en CI.
   > La task que sobrevive al reinicio acabó en `Talent.Infrastructure.Tests` (16 sep 2026), no aquí:
   > `RealServerFixture` es una fixture de colección, y reiniciar Postgres por debajo del resto de
   > clases no es defendible. El test levanta su propio contenedor con puerto fijo — con el mapeo
   > aleatorio, un stop-start puede devolver otro puerto y el store fallaría al reconectar por una
   > razón ajena a su lógica. Medido: sobrevive y reconecta en ~1 s.

---

## Riesgos

| Riesgo | Mitigación |
|---|---|
| ~~Native AOT vs descubrimiento por reflexión~~ | **Resuelto (27 ago 2026)** tal como estaba escrito: el trimming sí lo rompe, y en silencio. Se corta en el paso 2 de la cascada — registro explícito con `WithTools<T>()`, sin necesidad de bajar a JIT. [`ADR-0002`](../adr/0002-native-aot-and-explicit-tool-registration.md) |
| ~~**Native AOT vs EF Core**~~ (riesgo nuevo 27 ago 2026, **cerrado 10 sep 2026**) | Se materializó, y peor de lo previsto. ADR-0004 respondió la pregunta de F1/F2 —el host stdio **sí** necesita EF Core— y F6 lo midió: la build recortada **no arranca**, pero no por EF Core. Falla en la generación del schema de las tools, porque el SDK refleja sobre los tipos de parámetro y un enum del dominio no tiene `JsonTypeInfo` una vez recortado. **El analizador de trimming nunca avisó de eso**: reportó dos diagnósticos de EF Core y calló sobre lo que realmente rompió. AOT se corta, como el orden del plan preveía. [`ADR-0007`](../adr/0007-trim-clean-over-native-aot.md) |
| ~~El plan se apoya en notas de 2.0.0, no de 2.2.0~~ | **Resuelto (27 ago 2026).** Sin cambios rompientes; cinco hallazgos y una decisión nueva. [`Revisión del changelog`](../verification/sdk-2.0.0-to-2.2.0-review.md) · [`ADR-0001`](../adr/0001-streamable-http-session-mode.md) |
| ~~Keycloak sin `S256` en su metadata~~ | **Resuelto (27 ago 2026).** Realm versionado en `deploy/keycloak/realm.json` y verificado contra el stack corriendo: `code_challenge_methods_supported` es `["plain","S256"]`. Ojo — `plain` no se puede quitar del metadata del realm, la imposición es por cliente; el test asserta *presencia* de S256, no igualdad. [`deploy/keycloak/README.md`](../../deploy/keycloak/README.md) |
| La spec se sigue moviendo rápido | Fijar la revisión en el README y en un test; la política de deprecación da 12 meses de ventana |
| Alcance real de ~4 semanas | Orden de corte explícito: AOT → librería → Keycloak a API key |

---

## Verificación

```bash
docker compose up -d                            # Postgres, Keycloak, OTel, Jaeger, Prometheus, Grafana, servidor
dotnet test tests/Talent.Architecture.Tests     # regla de dependencia (no necesita el compose)
dotnet test tests/Talent.Domain.Tests           # dominio puro, sin Docker
dotnet test                                     # todo: + tools, conformidad y E2E
dotnet run --project bench/Talent.Mcp.Bench -c Release   # BenchmarkDotNet sobre el dominio puro
./scripts/measure-startup.sh                            # cold start y memoria (necesita Postgres)
```

Comprobaciones de aceptación. Se escribieron como manuales; cinco de las seis acabaron automatizadas,
que es mejor: una comprobación manual solo se hace la primera vez.

| # | Qué | Estado a 16 sep 2026 |
|---|---|---|
| 1 | `dotnet tool install -g` + configurar en Claude Code por stdio → las 6 tools y datos sembrados | ⚠️ **Parcial.** CI instala el tool y verifica las seis por nombre contra Postgres vivo; configurarlo en un cliente real no lo ha hecho nadie |
| 2 | `reject_candidate` sin razón → `input_required`; el reintento la ejecuta | ✅ E2E, con los dos caminos degradados |
| 3 | `bulk_score_shortlist`, reiniciar el contenedor, task consultable | ✅ `TaskStoreSurvivesDatabaseRestartTests`. **Sobrevive y reconecta en ~1 s** |
| 4 | Traza que cruza cliente → servidor → Postgres en un árbol | ✅ en sustancia: `A_meta_traceparent_survives_postgres_keycloak_and_a_real_socket`. Verlo en Jaeger sigue siendo cosa de ojos |
| 5 | Sin token → 401; sin el scope destructivo → denegado | ✅ E2E |
| 6 | `tools/list` dos veces → mismo orden, `ttlMs`/`cacheScope` presentes | ✅ Conformidad |

La 3 era la única sin cubrir, y era la que este plan describe como la que *justifica la existencia del
store de Postgres*. La respuesta, medida, es que sí.

---

## Fuentes

- [The 2026-07-28 Specification](https://blog.modelcontextprotocol.io/posts/2026-07-28/)
- [Key Changes — 2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28/changelog)
- [modelcontextprotocol/csharp-sdk](https://github.com/modelcontextprotocol/csharp-sdk)
- [NuGet: ModelContextProtocol 2.2.0](https://www.nuget.org/packages/ModelContextProtocol)
- [Announcing v2.0 of the official MCP C# SDK — .NET Blog](https://devblogs.microsoft.com/dotnet/announcing-v20-of-the-official-mcp-csharp-sdk/)
- [Release v2.0.0 — csharp-sdk](https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.0.0)
- [MCP C# SDK 2.0: Stateless HTTP, Interactive Tools and a Practical Migration Path](https://benjamin-abt.com/blog/2026/08/03/mcp-csharp-sdk-2/)
