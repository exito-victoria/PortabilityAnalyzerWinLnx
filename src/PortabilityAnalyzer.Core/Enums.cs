namespace PortabilityAnalyzer.Core;

/// <summary>Severidad de un hallazgo (orden creciente de impacto).</summary>
public enum Severity { Info, Bajo, Medio, Alto, Bloqueante }

/// <summary>Confianza de la deteccion.</summary>
public enum Confidence { Alta, Media, Baja }

/// <summary>Como aplica el motor el patron de una regla.</summary>
public enum PatternKind
{
    PInvokeDll,
    AssemblyReference,
    Namespace,
    Type,
    Method,
    Attribute,
    ApiCall,
    StringLiteral
}

/// <summary>Modo de comparacion de cadenas.</summary>
public enum MatchMode { Equals, StartsWith, Contains, Regex }

/// <summary>Clasificacion previa de un ensamblado.</summary>
public enum AssemblyKind { Managed, Native, KnownWindows, Unreadable }

/// <summary>
/// Estrategia para volver multiplataforma el codigo afectado por una regla (objetivo: core .NET 8
/// comun + WPF en Windows y Avalonia en Linux).
/// </summary>
public enum SeparationStrategy
{
    /// <summary>La libreria/codigo puede ser comun a ambos SO tal cual (ver notaComun para el como).</summary>
    Comun,
    /// <summary>Aislar tras una interfaz con implementacion por SO (Windows real + alternativa Linux).</summary>
    AbstraerPorPlataforma,
    /// <summary>Sustituir la dependencia por una equivalente multiplataforma o nativa de Linux.</summary>
    ReemplazarDependencia,
    /// <summary>Requiere trabajo de arquitectura/rediseno (caso tipico: UI WPF -> Avalonia en Linux).</summary>
    RedisenoUI
}
