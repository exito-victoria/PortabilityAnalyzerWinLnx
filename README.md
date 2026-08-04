# PortabilityAnalyzer

Herramienta de consola (.NET 8) que analiza estáticamente los ensamblados de una solución
Windows (WPF / Oracle) y evalúa la viabilidad de portar la lógica de negocio a Linux.

Detecta dependencias del sistema operativo Windows (P/Invoke, COM, referencias a ensamblados
solo-Windows, atributos de plataforma, registro, criptografía CAPI/CNG/DPAPI, identidad de
Windows, hilos/sincronización, invocación de comandos del SO, supuestos del sistema de ficheros
y driver Oracle), clasifica cada hallazgo por severidad y estima el esfuerzo de migración con un
modelo de tres puntos (PERT).

> **Estado: funcional.** Compila (SDK .NET 10 instalado; proyectos en `net8.0`) y se ha ejecutado
> sobre soluciones reales (WPF-Samples y la propia solución). Restaurar paquetes NuGet al abrirlo en
> Visual Studio 2022 y **verificar las versiones** de los `.csproj` si difieren del entorno.

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
  [--assume-third-party] [--third-party-factor <n>]
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

- El `IProjectDiscovery` para `.sln` (`SolutionProjectDiscovery`) usa una heurística por **nombre de
  ensamblado**: casa el nombre del proyecto (`AssemblyName` o nombre del `.csproj`) con las DLL de
  `bin`. No resuelve `ProjectReference` transitivas ni la carpeta de salida exacta por configuración.
- El detector de API (`ApiCallDetector`) marca constructores como `Mutex::.ctor` sin distinguir aún
  la sobrecarga **con nombre**; refinar inspeccionando los argumentos para reducir falsos positivos.
- Los esfuerzos del catálogo son **semilla orientativa**: recalibrar con datos reales.
- La API de `JsonSchema.Net` puede variar entre versiones; verificar el bloque de validación.

### Hecho recientemente

- **`IProjectDiscovery`** desde `.sln` con detección de terceros por ensamblado.
- Exclusión de carpetas `obj/` en el descubrimiento (evita contar duplicados solo-metadatos).
- Reglas de confianza **Baja** → sección "revisión manual", **excluidas** del esfuerzo y la severidad.
- Catálogo normalizado: `esBloqueante: true` ⟹ `severidad: "Bloqueante"`.
- Escape de celdas en el informe Markdown y `matchTimeout` en las regex del `PatternMatcher`.
