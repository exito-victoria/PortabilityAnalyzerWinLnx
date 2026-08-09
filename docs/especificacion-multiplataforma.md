# Especificación — Evaluación de coste para hacer la aplicación **multiplataforma** (.NET 8, Windows + Linux)

> Documento vivo de diseño. Sustituye al objetivo original de "análisis de portabilidad" por el de
> **estimar el impacto y el coste de tener la aplicación ejecutándose en Windows y en Linux** desde
> un **core .NET 8 compartido**, manteniendo el UI WPF actual en Windows e implementando un UI para
> Linux. Redactado para que el asistente **planifique la arquitectura antes de escribir código**.

---

## 1. Rol

Arquitecto de software senior en el ecosistema Microsoft (.NET/C#), especializado en:
- Migración y **multiplataforma** de aplicaciones .NET (Windows ↔ Linux, .NET 8).
- Separación de un **core portable** frente a **capas específicas de plataforma** (UI, interop, SO).
- Análisis estático de ensamblados IL y detección de dependencias de plataforma.
- Aplicaciones de escritorio industriales donde la **trazabilidad del dato es crítica**.

Prioriza corrección, mantenibilidad y trazabilidad. Cuando una API o versión de paquete pueda haber
cambiado, decláralo como suposición a verificar en lugar de asumirlo.

---

## 2. Contexto y objetivo

### 2.1 Historia de la aplicación
- Nació como aplicación **WPF**.
- Se actualizó hasta **.NET Framework 4.7 / 4.8**.
- Posteriormente se **migró a .NET 8** (`net8.0-windows`), solución de VS 2022 con ~39 proyectos
  (algunos de terceros), con acceso a **Oracle mediante ODP.NET**.

### 2.2 Nuevo objetivo (este es el cambio importante)
Ya **no** se trata de decidir si se puede portar la lógica. El objetivo es **establecer el impacto y
el coste de hacer la aplicación multiplataforma en .NET 8**, de modo que:

- Exista un **core común** (`net8.0`, sin `-windows`) que compile y se ejecute en **Windows y Linux**.
- Se **mantenga el UI WPF** en Windows.
- Se **implemente un UI para Linux** (estrategia asumida: **Avalonia**, por su cercanía a WPF —
  XAML/MVVM— y su capacidad de reutilizar buena parte de la UI). WPF sigue en Windows; Avalonia en
  Linux; ambos sobre el mismo core.

El análisis sigue siendo **estático** sobre los ensamblados compilados y los metadatos de
proyecto/NuGet; no se ejecuta la aplicación.

---

## 3. Qué debe estimar la herramienta

El informe debe permitir responder: **¿cuánto cuesta (impacto + horas) tener esto corriendo en
Windows y Linux?** Para ello el modelo de esfuerzo debe descomponerse en, al menos, estos **buckets**:

1. **Separación del core por plataforma.** Coste de extraer la lógica portable y aislar lo específico
   de Windows detrás de **abstracciones** (interfaces + inyección de dependencias con una
   implementación por SO). Ej.: registro, P/Invoke, rutas, identidad, criptografía CAPI/DPAPI.
2. **Reimplementación del UI de Linux** (Avalonia): portar vistas/estilos/bindings desde WPF; lo que
   se reutiliza y lo que hay que reescribir.
3. **Reemplazo/adaptación de dependencias** (ver §5): sustituir una DLL por una equivalente
   multiplataforma o nativa de Linux, o abstraerla.
4. **Pruebas y CI en ambos SO**: build y test en Windows y Linux.

Para **cada dependencia/hallazgo**, además del esfuerzo (PERT: O / Media / P), debe indicarse su
**estrategia de manejo**:

- **Común (portable tal cual)** → usar en el core; indicar cómo (p. ej. cambiar `net8.0-windows` a
  `net8.0`, evitar APIs `[SupportedOSPlatform("windows")]`, o proteger llamadas con
  `OperatingSystem.IsWindows()`).
- **Reemplazable** → proponer la librería/paquete multiplataforma o nativo de Linux equivalente.
- **A abstraer** → aislar tras una interfaz con implementación por SO (Windows real + Linux
  alternativa o *no-op*).
- **Bloqueante de rediseño** → requiere trabajo de arquitectura (caso típico: el **UI WPF**, que no
  corre en Linux y se cubre con Avalonia).

El modelo de esfuerzo es **configurable** y debe quedar **documentado** en el propio informe (sin
números "mágicos"), incluyendo el factor de incertidumbre aplicado a terceros.

---

## 4. Detalle exigido por *issue* (paso a paso)

Para **todos** los hallazgos (no solo los bloqueantes), el informe debe incluir:

- **Dónde se ha encontrado**, con trazabilidad completa: ensamblado → tipo → método → offset IL, y
  la **evidencia** concreta (DLL de P/Invoke, atributo, API llamada, literal…).
- **Pasos concretos para solucionarlo** (remediación), específicos de la categoría (p. ej. "sustituir
  `Microsoft.Win32.Registry` por configuración externa `appsettings.json` + `IConfiguration`; si se
  necesita en Windows, abstraer tras `ISettingsStore` con implementación de registro solo en Windows").
- **Estrategia de separación**: si el código afectado va al **core común** o a una **capa específica
  de plataforma**, y cómo.
- **Alternativa Linux propuesta** (reemplazo por librería compatible o nativa de Linux).
- **Esfuerzo de adaptación** (horas, PERT).

---

## 5. Software de terceros (sin fuentes) — análisis en profundidad

Para las DLLs de terceros de las que **no controlamos cómo fueron construidas**:

- Analizar sus **dependencias del SO** (P/Invoke, DLLs nativas Windows como `OraOps18.dll`,
  `kernel32`, `advapi32`; COM; registro) recorriendo su IL.
- **Proponer el reemplazo o estrategia multiplataforma** (p. ej. `Oracle.DataAccess`/
  `System.Data.OracleClient` → `Oracle.ManagedDataAccess.Core`).
- **Marcar el riesgo** derivado de no tener el código fuente ni control de su build, y recomendar
  acción (actualizar a una versión multiplataforma del paquete, reemplazar, o abstraer el uso).
- (Fase posterior, opcional) análisis **transitivo** de dependencias de dependencias.

---

## 6. Motor de análisis (base ya construida)

Análisis **estático IL con Mono.Cecil** (sin decompilar a C#). Clasificación previa de cada
ensamblado (gestionado / nativo PE / Windows-BCL conocido / no analizable) y detectores desacoplados
(`IAssemblyDetector`), como mínimo: **P/Invoke, COM, referencias solo-Windows (WPF/WinForms/WMI/
DirectoryServices/ServiceProcess/EventLog/PerformanceCounter/Drawing), atributos de plataforma,
Registro, invocación de comandos del SO, supuestos de sistema de ficheros, hilos/sincronización
(STA, Dispatcher, Mutex/Semaphore con nombre), Oracle (unmanaged→managed) y otros.**

### Catálogo de reglas (data-driven)
El conocimiento vive en un **catálogo JSON externo** validado por **JSON Schema**. Cada regla incluye:
`id`, `categoria`, `patron`, `severidad`, `esBloqueante`, `alternativaLinux`, `esfuerzo`, `confianza`,
y —**ampliación de la Fase 1, ya hecha** para dar el paso a paso de §3–§4:
- `pasosRemediacion`: lista de pasos concretos para resolverlo.
- `estrategiaSeparacion`: `Comun` | `AbstraerPorPlataforma` | `ReemplazarDependencia` | `RedisenoUI`.
- `notaComun`: si la librería puede ser común, cómo manejarlo (TFM, guardas de SO, DI).

Las **62 reglas** están pobladas y el **JSON Schema se amplió** en consecuencia, manteniendo la
compatibilidad del cargador. El **desglose de esfuerzo por bucket** (Fase 2) no se almacena por regla:
se deriva de la `estrategiaSeparacion` de cada hallazgo.

---

## 7. Documento de salida (requisitos)

Se generan **dos capas**:

**A) JSON legible por máquina** (contrato estable, versionado): metadatos, por ensamblado → hallazgos
con trazabilidad completa, clasificación y esfuerzo. Conserva **cada ocurrencia** por separado.

**B) Informes legibles por humanos** — **Markdown** y **Word (.docx)** con:
1. **Resumen ejecutivo**: nº ensamblados, bloqueantes, esfuerzo total O/Media/P y por bucket.
2. **Detalle por ensamblado**: origen (propio/tercero/nativo/Windows-conocido), y por dependencia:
   evidencia, **esfuerzo**, **alternativa Linux (reemplazo propuesto)**, **pasos de remediación** y
   **estrategia de separación** (común vs por-plataforma).
3. Separación clara de: bloqueantes de UI (WPF) · terceros sin fuentes · propios.
4. Riesgos y recomendaciones (empezando por UI WPF→Avalonia y Oracle unmanaged→managed).
5. Apéndice de metodología (catálogo y modelo de estimación).

### Requisitos concretos del reporte (obligatorios)
- **Una única ejecución debe poder producir varios informes** a la vez (p. ej. **Word y Markdown**).
  `--format` acepta lista separada por comas (`word,markdown`) o `all`; con varios formatos, la
  extensión de cada fichero se deriva de `--output`.
- **Sin datos repetidos**: cada dependencia aparece **una sola vez** (ocurrencias idénticas agregadas
  con su recuento). Los ensamblados se **deduplican por nombre** (misma DLL en varios `bin` → una vez).
- **Ajuste de tablas en Word**: las tablas **no deben desbordarse del tamaño de la hoja**. Requisito
  técnico: página **apaisada**, **layout de tabla fijo** (`tblLayout=fixed`) con **anchos de columna
  explícitos** que sumen el **ancho útil** de la página (ancho de página menos márgenes), y **fuente
  de celda reducida**; las columnas de texto largo (evidencia, alternativa Linux) ajustan por línea.

---

## 8. Entradas (CLI)

- `--path <.sln | .csproj | directorio | .dll/.exe>` (una **solución completa** o un **proyecto
  concreto**; también directorio o ensamblado suelto).
- `--rules <catálogo.json>` y `--schema <schema.json>` (valida el catálogo antes de analizar).
- `--output <ruta base>` y `--format <json|markdown|word|all|lista>`.
- `--assume-third-party`, `--third-party-factor <n>` para el factor de incertidumbre cuando no hay
  resolución de proyecto; `--testing-factor <n>` para la fracción de Pruebas y CI (0.25 por defecto).
- Las carpetas intermedias `obj/` (con `ref/`/`refint/`) se excluyen; los apphost `.exe` de .NET
  moderno se descartan si existe su `.dll` hermano.

---

## 9. Arquitectura y calidad (requisito)

- **Capas**: *Discovery* (sln/csproj/DLLs/NuGet) · *AnalysisEngine* (detectores enchufables) ·
  *RuleCatalog* (data-driven + schema) · *Scoring/Estimation* · *Reporting* (exportadores
  intercambiables: JSON/Markdown/Word).
- Detectores como interfaces registrables; **logging estructurado** (Serilog); **robustez** (nunca
  aborta por una DLL corrupta/nativa; la registra y sigue); **regex con timeout** (anti-ReDoS).
- **Testeable**: detección y estimación con pruebas unitarias.
- Paquetes estables actuales verificados contra .NET 8 (Mono.Cecil, JsonSchema.Net, Serilog,
  DocumentFormat.OpenXml para Word).

---

## 10. Plan por fases

- **Fase 0 — Hecho.** Analizador base; descubrimiento `.sln`/`.csproj`; exclusión `obj/` y apphosts;
  dedup por nombre de ensamblado; catálogo validado por schema; informes **Markdown y Word** con
  **esfuerzo** y **alternativa Linux** por dependencia; **una ejecución → varios informes**; **tablas
  Word ajustadas a la hoja** (apaisado + layout fijo).
- **Fase 1 — Hecho.** Catálogo enriquecido: `pasosRemediacion`, `estrategiaSeparacion`
  (`Comun`/`AbstraerPorPlataforma`/`ReemplazarDependencia`/`RedisenoUI`) y `notaComun`; JSON Schema
  ampliado; **las 62 reglas pobladas**. En el informe, cada dependencia muestra **dónde se encontró**
  (ubicación), estrategia, alternativa Linux y **pasos de remediación** (para todos los issues, no
  solo bloqueantes).
- **Fase 2 — Hecho.** Modelo de coste por **buckets** (adaptación a núcleo común · separación por
  plataforma · reemplazo de dependencias · UI Linux Avalonia · **pruebas y CI**) con resumen ejecutivo
  O/Media/P y %. `--testing-factor` configurable (0.25 por defecto), documentado en el informe.
- **Fase 3 — Hecho.** Análisis de terceros (sin fuentes): inventario de **dependencias nativas del
  SO** por DLL de terceros (P/Invoke, por sitio de llamada), clasificadas (sistema Windows vs nativa
  de terceros a verificar en Linux), marca de riesgo y **reemplazo sugerido** (p. ej. Oracle.DataAccess
  → Oracle.ManagedDataAccess.Core).
- **Fase 4 — Hecho.** El informe genera una **recomendación de arquitectura destino** concreta:
  estructura de proyectos (`Core` net8.0 · `Abstractions` · `Platform.Windows`/`Platform.Linux` ·
  `App.Windows` WPF / `App.Linux` Avalonia), la **capa de abstracción** derivada de los hallazgos
  (`ISettingsStore`, `IUserIdentity`, `IInterProcessLock`, `INativePlatform`, …) y un **plan de
  migración** por pasos, con el esfuerzo total (incluidas Pruebas y CI) y el nº de bloqueantes.
- **Fase 5 — Hecho (requisitos del cliente).** Ver §12. Hecho: análisis a nivel de **código fuente**
  (Roslyn) con segmento y corrección; **impacto por proyecto** (clases/ficheros afectados); cabecera
  con la **estrategia de estimación** (en horas); columna **N** aclarada; título/lenguaje
  **multiplataforma**; **roles de proyecto** (`--roles`) con API obligatoria, no modificables (su
  esfuerzo no se imputa) y divisibles por UI; equivalente concreto en el paso de reemplazo; **bug de
  tildes** corregido. **Pendiente**: investigación específica del soporte Linux de los paquetes de
  ACRA/XMA/Safran (requiere sus DLLs/paquetes reales, disponibles en el análisis del cliente).

---

## 11. Instrucciones de trabajo

1. **Planifica antes de implementar**; señala trade-offs y riesgos de precisión.
2. Implementa **por incrementos** y **por fases** (§10), construyendo y validando en cada paso.
3. Donde una API o comportamiento dependa de la versión de .NET o de un paquete, **decláralo como
   suposición a verificar**.
4. Trabaja en la **rama `multiplataformWnLx`** para este bloque de cambios.

---

## 12. Requisitos del cliente (batch prueba-cliente)

**Objetivo urgente:** que la **API** sea multiplataforma **Windows ↔ Linux** — en concreto
**`ProgrammingManagerService`** y **`ProgrammingManagerLib`** (de ellas dependen **FIDA**, el **PRM
legacy** y el **ICD importer legacy**). "Multiplataforma general" (más allá de Windows-Linux) es una
**ampliación de alcance posterior**; el foco actual sigue siendo Windows↔Linux vía API.

1. **Análisis a nivel de código fuente (nuevo).** Además del IL, leer los `.cs` con **Roslyn**
   (análisis sintáctico) y localizar los usos de APIs propias de Windows (directivas `using`, tipos,
   llamadas a métodos). Por cada uso: **fichero + línea**, el **segmento de código** afectado, el
   `using`/clase/método implicado y **cómo corregirlo** para multiplataforma. Aplica a `.sln` y a
   `.csproj`. En desarrollo se prueba con los ejemplos (que tienen `.cs`); el análisis real será sobre
   `ProgrammingManagerService`, `ProgrammingManagerLib` y `ToolsCommon`, cuyo código fuente estará
   disponible en tiempo de ejecución.
2. **Métrica de impacto (además de las horas).** Por paquete (proyecto/ensamblado): **recuento y lista
   de clases y ficheros afectados**, con desglose por **espacio de nombres**. Da idea del tamaño del
   cambio independientemente de las horas.
3. **Cabecera del informe.** Incluir una **descripción concisa de la estrategia de estimación** (PERT
   O/Media/P; factor de terceros; Pruebas y CI) y **aclarar explícitamente que las cifras son horas**.
   El rango optimista–pesimista es ancho por naturaleza: explicarlo.
4. **Columna `N`.** Aclarar su significado en el informe: **nº de ocurrencias agregadas** de la misma
   regla+evidencia (se muestra una fila por dependencia y `N` indica cuántas veces aparece).
5. **Lenguaje multiplataforma.** Redactar en términos de **multiplataforma** (no solo el par
   Windows-Linux), manteniendo el foco urgente en Windows↔Linux vía API.
6. **Roles de proyecto — fichero de configuración `--roles` (JSON).** Define el papel de cada proyecto
   por nombre. La recomendación de arquitectura y las métricas usan estos roles:
   - **`obligatorioMultiplataforma`** (`ProgrammingManagerService`, `ProgrammingManagerLib`): hoy
     **no son API**; deben **convertirse en una API multiplataforma**. Prioridad máxima; sus
     bloqueantes son los críticos.
   - **`noModificables`** (`ACRA`, `XMA`, `Safran`): de terceros. Se **analizan** sus DLLs, pero **no
     podemos hacerlas multiplataforma nosotros** — es responsabilidad del **proveedor**; su esfuerzo
     **no se cuenta como nuestro**. Se indica si existe versión/soporte Linux.
   - **`divisiblePorUI`** (`ToolsCommon`): es nuestra y hay que **dividirla**: extraer **todo lo que
     depende de Windows** a un **proyecto nuevo** y dejar el resto **limpio/multiplataforma**. Al
     analizarla, el informe **propone explícitamente esa separación** (qué va a la parte limpia y qué
     a la parte Windows).
   Ejemplo de fichero:
   ```json
   {
     "obligatorioMultiplataforma": ["ProgrammingManagerService", "ProgrammingManagerLib"],
     "noModificables":            ["ACRA", "XMA", "Safran"],
     "divisiblePorUI":            ["ToolsCommon"]
   }
   ```
7. **Proveedores externos (ACRA, XMA, Safran).** No nuestros: **no se pueden migrar ni modificar** (lo
   debe hacer el proveedor). Aun así, analizar sus DLLs e **investigar si existe soporte/versión
   Linux** del paquete; el informe incluye la **restricción** y la **vía viable** (o su ausencia), y su
   esfuerzo **no se imputa** al total nuestro.

**Contrato de salida:** se **mantiene**; estos cambios **añaden** campos/secciones. La columna `N`
(solo Markdown/Word) se **aclara**, no se elimina.

**Nota — bug corregido:** las tildes del informe salían con caracteres raros (`autenticaciÃ³n`) por
un problema de codificación al poblar el catálogo (PowerShell 5.1 leía el script como Windows-1252);
corregido — el catálogo queda en UTF-8 correcto.
