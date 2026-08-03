using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace PortabilityAnalyzer.Core;

/// <summary>Aplica el patron de una regla a un candidato textual, con cache de expresiones regulares.</summary>
public static class PatternMatcher
{
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new();

    /// <summary>Cota de tiempo por evaluacion de regex: evita colgar el analisis ante un patron con
    /// backtracking catastrofico (ReDoS) sobre literales de ensamblados de terceros no confiables.</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    public static bool Matches(RulePattern pattern, string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;

        return pattern.MatchMode switch
        {
            MatchMode.Equals     => string.Equals(candidate, pattern.Valor, StringComparison.Ordinal),
            MatchMode.StartsWith => candidate.StartsWith(pattern.Valor, StringComparison.Ordinal),
            MatchMode.Contains   => candidate.Contains(pattern.Valor, StringComparison.Ordinal),
            MatchMode.Regex      => RegexMatches(pattern.Valor, candidate),
            _ => false
        };
    }

    private static bool RegexMatches(string pattern, string candidate)
    {
        try
        {
            return GetRegex(pattern).IsMatch(candidate);
        }
        catch (RegexMatchTimeoutException)
        {
            // El patron tardo demasiado sobre este candidato: se degrada a "sin coincidencia"
            // en lugar de propagar y descartar el resto de hallazgos del detector.
            return false;
        }
    }

    private static Regex GetRegex(string pattern) =>
        RegexCache.GetOrAdd(pattern, p => new Regex(p, RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexTimeout));
}
