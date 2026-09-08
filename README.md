# PortabilityAnalyzer

Herramienta de consola (.NET 8) que analiza estáticamente una solución/proyecto de Windows y
**estima el impacto y el coste de hacerla multiplataforma en .NET 8**, con enfoque **portable-first**:
llevar todo lo posible a un **núcleo portable** (`net8.0`) y **aislar únicamente lo que obligatoriamente
depende de Windows**, dejándolo **preparado para que otro equipo aporte la parte no-Windows**. El
análisis **no prescribe** la plataforma destino ni implementa la UI de otro SO: separa, estructura y deja
listo el punto de extensión.

Detecta dependencias del sistema operativo Windows a nivel de **IL** (Mono.Cecil: P/Invoke, COM,
referencias a ensamblados solo-Windows, atributos de plataforma) y a nivel de **código fuente** (Roslyn:
`using`/tipos/atributos, con fichero, línea, clase/método, segmento y cómo corregir). Cubre Registro,
criptografía CAPI/CNG/DPAPI, identidad de Windows, WMI, EventLog, servicios, hilos/sincronización,
invocación de comandos del SO, supuestos del sistema de ficheros y drivers de base de datos.

> **Estado: funcional.** Compila (`net8.0`) y se ha ejecutado sobre soluciones reales (WPF, WinForms/Oracle
> y una solución sintética). Al abrirlo en Visual Studio 2022, restaurar los paquetes NuGet. Los `.cs` se
> guardan en **UTF-8 con BOM** para que las tildes se lean bien en cualquier compilador.

## Estructura

```
PortabilityAnalyzer.sln
rules/
  reglas_portabilidad_windows_linux.json   # catálogo semilla (62 reglas)
  portability-rules.schema.json            # JSON Schema que valida el catálogo
  roles-proyecto.json                      # ejemplo de roles de proyecto (--roles)
src/
  PortabilityAnalyzer.Core/        # dominio: modelos, enums, roles, orden de compilación, resultados
  PortabilityAnalyzer.Rules/       # carga del catálogo + validación contra el schema
  PortabilityAnalyzer.Engine/      # detectores IL (Mono.Cecil) + análisis de código (Roslyn), estimador, split
  PortabilityAnalyzer.Reporting/   # exportadores JSON, Markdown, Word y el informe ejecutivo
  PortabilityAnalyzer.Cli/         # punto de entrada de consola
```

Capas: `Core` no depende de nada; `Rules`, `Engine` y `Reporting` dependen de `Core`; `Cli` compone todo.

## Prerrequisitos

- .NET 8 SDK
- Visual Studio 2022 (o `dotnet` CLI)

## Paquetes NuGet

| Proyecto   | Paquete                                                        |
|------------|----------------------------------------------------------------|
| Rules      | `JsonSchema.Net`                                               |
| Engine     | `Mono.Cecil`, `Microsoft.CodeAnalysis.CSharp` (Roslyn), `Serilog` |
| Reporting  | `DocumentFormat.OpenXml` (Word .docx)                          |
| Cli        | `Serilog`, `Serilog.Sinks.Console`                             |

## Uso

```
PortabilityAnalyzer.Cli \
  --path    <.sln | .csproj | directorio con las DLL | ruta a una DLL/EXE> \
  --rules   rules/reglas_portabilidad_windows_linux.json \
  --schema  rules/portability-rules.schema.json \
  --roles   rules/roles-proyecto.json \
  --output  salida/informe.docx \
  --format  word,markdown \
  --executive \
  [--assume-third-party] [--third-party-factor <n>] [--testing-factor <n>]
```

| Opción | Descripción |
|--------|-------------|
| `--path` | Solución (`.sln`), proyecto (`.csproj`), directorio con DLLs o una DLL/EXE. |
| `--rules` / `--schema` | Catálogo de reglas y (opcional) su JSON Schema. Si se indica, valida el catálogo **antes** de analizar. |
| `--roles` | Roles de proyecto (JSON). Habilita la recomendación por rol y el **generador de división**. |
| `--output` | Ruta del informe. La carpeta se **crea si no existe**. |
| `--format` | `json` (por defecto), `markdown`, `word`, `all`, o lista por comas (`word,markdown`). Con varios, la extensión de cada fichero se deriva de `--output`. |
| `--executive` | Genera además el **informe ejecutivo** `InformeEjec_<proyecto>.docx` en la misma carpeta. |
| `--assume-third-party` | Aplica el factor de incertidumbre a todos los ensamblados (para directorio/DLL sueltos). |
| `--third-party-factor <n>` | Fija el factor (>0; implica `--assume-third-party`; 1.5 por defecto). |
| `--testing-factor <n>` | Fracción del esfuerzo imputada a Pruebas y CI (0.25 por defecto). |

Con `.sln` se analizan todos sus proyectos; con `.csproj`, solo ese. Las salidas se **deduplican por
nombre de ensamblado** y se excluyen `obj/`, `bin/` y apphosts.

## Roles de proyecto (`--roles`)

Fichero JSON que asigna un papel a cada proyecto (coincidencia por nombre, flexible):

```json
{
  "obligatorioMultiplataforma": ["ProgrammingManagerService", "ProgrammingManagerServiceLib"],
  "noModificables":            ["ACRA", "XMA", "Safran"],
  "divisiblePorUI":            ["ToolsCommon"],
  "separables":                []
}
```

- **obligatorioMultiplataforma**: prioridad máxima; el informe da el **análisis de los cambios** para hacerlos portables.
- **noModificables**: de terceros; **no se migran** (es del proveedor), su esfuerzo **no se imputa**, y se dan **opciones viables** para ejecutarlos en el entorno destino.
- **divisiblePorUI** / **separables**: proyectos a **separar** en dos (ver "Generador de división").

## Los tres informes

- **General** (Markdown y Word): resumen, **coste por bloque**, **orden de compilación** (con Target Framework),
  **recomendación de arquitectura** con un **ejemplo de migración** real y la definición de «seam»,
  **análisis de terceros**, **terceros no modificables** (restricción + opciones), **impacto por proyecto**
  (clases y ficheros afectados), **análisis de código fuente** (dónde y cómo corregir) y un **apéndice de
  equivalencias portables / aislamiento por SO** con fragmentos de código. El Word incluye **Tabla de
  contenido** (con estilos de título) y **repite las cabeceras** de tabla al partir en páginas.
- **Ejecutivo** (`--executive`): Word breve para el **cliente** — resumen, cifras clave, **estimación por
  proyecto** distinguiendo Proyecto propio de **DLL de terceros** (con su autor), coste por bloque
  (optimista primero), hallazgos principales, restricciones y recomendación.
- **JSON**: contrato para integraciones (se **mantiene** estable; los cambios solo añaden).

La estimación es **PERT** a tres puntos (O = optimista, M = más probable, P = pesimista; media = valor
esperado); el esfuerzo se cuenta una vez por regla y ensamblado, con factor de incertidumbre para terceros
y un bucket transversal de **Pruebas y CI**.

## Generador de división de proyectos (split)

Para los proyectos con rol `divisiblePorUI`, `obligatorioMultiplataforma` o listados en `separables`, genera
en una subcarpeta dedicada **`proyectos-separados/`** (nunca colisiona con el código original) dos proyectos:

- **`<Nombre>Multi`** (`net8.0`, núcleo portable) y **`<Nombre>`** (`net8.0-windows`).
- Clasifica por fichero (hallazgos + herencia + clases parciales), copia también el **contenido**
  (XAML/resx/recursos), usa **namespace separado** para el núcleo y genera `GlobalUsings.cs`.
- **Seams por categoría**: interfaz portable en el núcleo + implementación Windows real (con su paquete NuGet).
- **Separación por método**: los métodos que usan API de Windows se envuelven en `#if WINDOWS` con un `#else`
  (stub) que **indica el hueco no-Windows**.
- **Umbral de portabilidad**: un fichero mayormente portable con **≤ 2 métodos** Windows y sin acoplamiento
  de clase **se queda en el núcleo** (que pasa a **multi-target** `net8.0;net8.0-windows` con paquetes Windows
  condicionales), y se genera una **interfaz (seam) por clase** para la separación limpia.
- **`SPLIT-NOTES-<Nombre>.md`**: guía de finalización paso a paso, con registro por DI, cambios por categoría
  (código antes/después) y la **separación por interfaces** explicada con las firmas reales.

Los proyectos generados **compilan** (validado); quedan listos a falta de conectar los seams y probar.

## Cómo funciona

1. **Clasificación** de cada ensamblado (gestionado / nativo / Windows-BCL omitido / ilegible).
2. **Detección IL** (Mono.Cecil) con trazabilidad ensamblado → tipo → método → offset, y **detección de
   código fuente** (Roslyn) con fichero/línea/segmento.
3. **Estimación** (PERT) por bucket, con factor de terceros y bucket de Pruebas y CI.
4. **Informe(s)** y, si procede, **generación de los proyectos separados**.

## Puntos de extensión

- **Añadir una regla** → editar el JSON del catálogo (el schema lo valida; no requiere recompilar).
- **Añadir un detector** → implementar `IAssemblyDetector` y registrarlo en `Program.cs`.
- **Añadir un formato de salida** → implementar `IReportExporter`.

## Limitaciones conocidas

- El descubrimiento `.sln`/`.csproj` casa por **nombre de ensamblado**; no resuelve `ProjectReference`
  transitivas ni la salida exacta por configuración.
- La separación por método cubre **métodos** (no propiedades/constructores); el resto queda documentado.
- Al separar, las **referencias a otros proyectos** de la solución hay que reañadirlas (queda anotado en el `SPLIT-NOTES`).
- Los esfuerzos del catálogo son **semilla orientativa**: recalibrar con datos reales.

---

> La especificación de trabajo detallada (objetivo, modelo de estimación y plan por fases) se mantiene como
> **documento interno en local**, fuera de este repositorio.
