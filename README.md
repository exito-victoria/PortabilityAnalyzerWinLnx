# PortabilityAnalyzer

Herramienta de consola (.NET 8) que analiza estáticamente los ensamblados de una solución Windows
(WPF / Oracle) y **estima el impacto y el coste de hacerla multiplataforma en .NET 8** (Windows +
Linux): un **core común** compartido, manteniendo el UI WPF en Windows e implementando el UI de Linux
con **Avalonia**. Ver [docs/especificacion-multiplataforma.md](docs/especificacion-multiplataforma.md)
para el objetivo, el modelo de estimación y el plan por fases.

Detecta dependencias del sistema operativo Windows (P/Invoke, COM, referencias a ensamblados
solo-Windows, atributos de plataforma, registro, criptografía CAPI/CNG/DPAPI, identidad de
Windows, hilos/sincronización, invocación de comandos del SO, supuestos del sistema de ficheros
y driver Oracle). Para cada dependencia indica **dónde se encontró**, su **estrategia de separación**
(común / abstraer por plataforma / reemplazar / rediseño de UI), la **alternativa Linux**, los
**pasos de remediación** y el **esfuerzo** (PERT). El informe incluye un **resumen de coste por
bucket** y un **análisis en profundidad de los ensamblados de terceros** (sin fuentes).

> **Estado: funcional.** Compila (SDK .NET 10 instalado; proyectos en `net8.0`) y se ha ejecutado
> sobre soluciones reales (WPF-Samples, WinForms/Oracle y la propia solución). Restaurar paquetes
> NuGet al abrirlo en Visual Studio 2022 y **verificar las versiones** de los `.csproj` si difieren
> del entorno.

## Estructura

```
PortabilityAnalyzer.sln
rules/
  reglas_portabilidad_windows_linux.json   # catálogo semilla (61 reglas)
  portability-rules.schema.json            # JSON Schema que valida el catálogo
src/
  PortabilityAnalyzer.Core/        # dominio puro: modelos, enums, PatternMatcher, interfaces
  PortabilityAnalyzer.Rules/       # carga del catálogo + validación contra el schema
  PortabilityAnalyzer.Engine/      # detectores (Mono.Cecil), clasificador, motor, estimador
  PortabilityAnalyzer.Reporting/   # exportadores JSON, Markdown y Word (.docx)
  PortabilityAnalyzer.Cli/         # punto de entrada de consola
```

Capas: `Core` no depende de nada; `Rules`, `Engine` y `Reporting` dependen de `Core`; `Cli`
compone todo. Solo `Engine` conoce Mono.Cecil, de modo que el dominio permanece independiente
del decompilador.

## Prerrequisitos

- .NET 8 SDK
- Visual Studio 2022 (o `dotnet` CLI)

## Paquetes NuGet

| Proyecto   | Paquete                              |
|------------|--------------------------------------|
| Rules      | `JsonSchema.Net` (json-everything)   |
| Engine     | `Mono.Cecil`, `Serilog`              |
| Reporting  | `DocumentFormat.OpenXml` (Word .docx)|
| Cli        | `Serilog`, `Serilog.Sinks.Console`   |

## Uso

```
PortabilityAnalyzer.Cli \
  --path    <.sln | .csproj | directorio con las DLL | ruta a una DLL/EXE> \
  --rules   rules/reglas_portabilidad_windows_linux.json \
  --schema  rules/portability-rules.schema.json \
  --output  informe \
  --format  word,markdown \
  [--assume-third-party] [--third-party-factor <n>] [--testing-factor <n>]
```

`--format` acepta `json`, `markdown`, `word`, `all`, o una **lista separada por comas**. Una sola
ejecución puede generar **varios informes** a la vez (p. ej. `word,markdown` → Word *y* Markdown); con
varios formatos, la extensión de cada fichero (`.docx`/`.md`/`.json`) se deriva de `--output`. El
informe **Word** usa página apaisada y tablas de ancho fijo para que **no se desborden de la hoja**.

Si se indica `--schema`, el catálogo se valida contra él **antes** de analizarse; un catálogo
mal formado detiene la ejecución con el detalle de los errores.

**Origen de los ensamblados y terceros:**

- Si `--path` apunta a un **`.sln`** o a un **`.csproj`**, se resuelven los proyectos implicados, se
  derivan los nombres de ensamblado propios y se marca como *de terceros* toda DLL de `bin` que no
  sea salida de un proyecto (típicamente paquetes NuGet). El factor de incertidumbre se aplica
  **solo** a esas. Con `.csproj` se analiza únicamente ese proyecto; con `.sln`, todos los suyos.
- Si `--path` es un **directorio o DLL/EXE**, no hay resolución de proyecto: por defecto todo se
  trata como propio (first-party). Usa `--assume-third-party` para aplicar el factor a todo, o
  `--third-party-factor <n>` para fijar su valor (implica `--assume-third-party`; por defecto 1.5).

Las salidas se **deduplican por nombre de ensamblado**: una misma DLL copiada en varios `bin` se
analiza (y aparece en el informe) una sola vez. Las carpetas intermedias `obj/` (con sus *reference
assemblies* `ref/` y `refint/`) se excluyen del escaneo.

**Coste multiplataforma y terceros.** El informe abre con un **resumen de coste por bucket**
(adaptación a núcleo común · separación por plataforma · reemplazo de dependencias · UI Linux
Avalonia · pruebas y CI) y una sección de **análisis de terceros** que inventaria las dependencias
nativas del SO de cada DLL de terceros y sugiere su reemplazo. `--testing-factor <n>` fija la fracción
del esfuerzo de desarrollo imputada a Pruebas y CI (0.25 por defecto).

**Formatos de salida:** `json` (contrato para integraciones, una entrada por ocurrencia),
`markdown` (legible, ocurrencias agregadas) y `word` (`.docx` nativo vía OpenXML). Markdown y Word
muestran, por dependencia, el **esfuerzo de adaptación** y la **alternativa Linux propuesta**
(reemplazo por una librería compatible o nativa de Linux).

## Cómo funciona

1. **Clasificación** de cada ensamblado: gestionado (analizable), nativo (PE sin metadatos CLI),
   Windows/BCL conocido (se omite) o ilegible.
2. **Carga** con Mono.Cecil (en memoria, sin símbolos, lectura diferida).
3. **Detección**: cada `IAssemblyDetector` recibe las reglas de los tipos de patrón que atiende y
   emite hallazgos con trazabilidad (ensamblado → tipo → método → offset IL).
4. **Estimación**: el esfuerzo se cuenta una vez por regla y por ensamblado (no se multiplica por
   ocurrencia) y se aplica un factor de incertidumbre a los ensamblados de terceros.
5. **Informe**: JSON para máquina, o Markdown/Word para humanos (con esfuerzo y alternativa Linux por dependencia).

## Puntos de extensión

- **Añadir una regla** → editar el JSON del catálogo. No requiere recompilar. El schema lo valida.
- **Añadir un detector** → implementar `IAssemblyDetector` (declarar los `PatternKind` que atiende
  en `Handles`) y registrarlo en `Program.cs`.
- **Añadir un formato de salida** → implementar `IReportExporter`.
- **Añadir una categoría nueva** → recordar actualizar también el `enum` de `categoria` en el schema.

## Limitaciones conocidas / TODO

- El `IProjectDiscovery` para `.sln`/`.csproj` (`ProjectDiscovery`) usa una heurística por **nombre de
  ensamblado**: casa el nombre del proyecto (`AssemblyName` o nombre del `.csproj`) con las DLL de
  `bin`. No resuelve `ProjectReference` transitivas ni la carpeta de salida exacta por configuración.
- El detector de API (`ApiCallDetector`) marca constructores como `Mutex::.ctor` sin distinguir aún
  la sobrecarga **con nombre**; refinar inspeccionando los argumentos para reducir falsos positivos.
- Los esfuerzos del catálogo son **semilla orientativa**: recalibrar con datos reales.
- La API de `JsonSchema.Net` puede variar entre versiones; verificar el bloque de validación.

### Hecho recientemente

Trabajo hacia el objetivo **multiplataforma** (rama `multiplataformWnLx`), por fases:

- **Fase 0** — Descubrimiento `.sln`/`.csproj`, exclusión de `obj/` y apphosts, dedup por nombre de
  ensamblado, dos informes en una ejecución y **tablas Word ajustadas a la hoja** (apaisado + fijo).
- **Fase 1** — Catálogo con `estrategiaSeparacion`, `pasosRemediacion` y `notaComun` (**62 reglas**);
  en el informe, **ubicación**, estrategia, alternativa y **pasos de remediación** por dependencia.
- **Fase 2** — **Coste por bucket** (núcleo común / separación / reemplazo / UI Linux / pruebas y CI)
  con `--testing-factor` configurable.
- **Fase 3** — **Análisis de terceros** (sin fuentes): dependencias nativas del SO por DLL y reemplazo
  sugerido.
- **Fase 4** — **Recomendación de arquitectura destino** y plan de migración: estructura de proyectos
  (`Core`/`Abstractions`/`Platform.*`/`App.Windows`-WPF/`App.Linux`-Avalonia), capa de abstracción
  derivada de los hallazgos y pasos de migración.

Base previa: reglas de confianza **Baja** → "revisión manual" (excluidas del esfuerzo); catálogo
normalizado (`esBloqueante` ⟹ `severidad: "Bloqueante"`); escape de celdas Markdown y `matchTimeout`
(anti-ReDoS) en `PatternMatcher`.
