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
