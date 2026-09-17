using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PortabilityAnalyzer.Engine;

/// <summary>
/// Rebases the namespaces of a SPLIT project so the namespace matches the new project name: files that go to
/// <c>X.Core</c> declare <c>namespace X.Core[.Sub]</c> and files that go to <c>X.Windows</c> declare
/// <c>namespace X.Windows[.Sub]</c>. It also updates every reference across the whole generated solution:
/// <c>using</c> directives and fully-qualified type/member references are rebased to the new namespaces.
///
/// Resolution rule that keeps it deterministic and correct:
/// - The Core/portable side references only Core namespaces (a portable project never depends on a Windows
///   one), so an old namespace of a split project resolves to its <c>.Core</c> form.
/// - The Windows side sees both its own <c>.Core</c> and <c>.Windows</c> namespaces, so a plain <c>using</c>
///   is expanded to whichever of the two actually exists (both if both do); a fully-qualified reference is
///   resolved to the longest existing namespace prefix.
/// Only the namespaces of split projects are touched; everything else is left exactly as-is.
/// </summary>
internal sealed class NamespaceRebaser
{
    public enum Side { Core, Windows }

    private sealed record Split(string OrigRoot, string CoreRoot, string WinRoot);

    private static readonly Regex DottedPath =
        new(@"^@?[A-Za-z_][A-Za-z0-9_]*(\.@?[A-Za-z_][A-Za-z0-9_]*)+$", RegexOptions.Compiled);

    private readonly List<Split> _splits;
    private readonly HashSet<string> _existing; // all NEW namespaces that actually exist after rebasing

    public NamespaceRebaser(IEnumerable<(string OrigRoot, string CoreRoot, string WinRoot)> splits, HashSet<string> existingNamespaces)
    {
        // Longer roots first so nested split roots (if any) match before their parents.
        _splits = splits.Select(s => new Split(s.OrigRoot, s.CoreRoot, s.WinRoot))
                        .OrderByDescending(s => s.OrigRoot.Length).ToList();
        _existing = existingNamespaces;
    }

    /// <summary>New namespace for an original namespace on a given side, or the same string if it does not
    /// belong to any split project.</summary>
    public string RebaseName(string ns, Side side)
    {
        foreach (var s in _splits)
        {
            var newRoot = side == Side.Core ? s.CoreRoot : s.WinRoot;
            if (string.Equals(ns, s.OrigRoot, StringComparison.Ordinal)) return newRoot;
            if (ns.StartsWith(s.OrigRoot + ".", StringComparison.Ordinal))
                return newRoot + ns.Substring(s.OrigRoot.Length);
        }
        return ns;
    }

    /// <summary>True if the namespace belongs to a split project (i.e. it would be rebased).</summary>
    public bool Belongs(string ns) => !string.Equals(RebaseName(ns, Side.Core), ns, StringComparison.Ordinal);

    private bool Exists(string ns) => _existing.Contains(ns);

    /// <summary>Rewrites the whole file (namespace declarations, usings and fully-qualified references) for the
    /// given side. Returns the original content unchanged if it cannot be parsed.</summary>
    public string Rewrite(string content, Side side)
    {
        SyntaxNode root;
        try { root = CSharpSyntaxTree.ParseText(content).GetRoot(); }
        catch { return content; }
        var rewritten = new Impl(this, side).Visit(root);
        return rewritten is null ? content : rewritten.ToFullString();
    }

    /// <summary>Candidate new namespaces for a plain <c>using</c> of <paramref name="ns"/> on a side. Empty
    /// means the namespace no longer exists (the using is dropped); a non-split namespace returns itself.</summary>
    private List<string> UsingCandidates(string ns, Side side)
    {
        if (!Belongs(ns)) return new List<string> { ns };
        var core = RebaseName(ns, Side.Core);
        var win = RebaseName(ns, Side.Windows);
        var res = new List<string>();
        if (side == Side.Core)
        {
            if (Exists(core)) res.Add(core);
        }
        else
        {
            if (Exists(win)) res.Add(win);
            if (Exists(core) && !string.Equals(core, win, StringComparison.Ordinal)) res.Add(core);
        }
        return res;
    }

    /// <summary>Single deterministic resolution for an alias/using-static/qualified reference.</summary>
    private string ResolveSingle(string ns, Side side)
    {
        if (!Belongs(ns)) return ns;
        var target = RebaseName(ns, side);
        if (Exists(target)) return target;
        var core = RebaseName(ns, Side.Core);
        return Exists(core) ? core : target;
    }

    /// <summary>Tries to rebase the namespace prefix of a fully-qualified dotted path (type or static member).
    /// Picks the longest leading segment run that is a known new namespace.</summary>
    private bool TryRebaseDotted(string full, Side side, out string result)
    {
        result = full;
        if (!DottedPath.IsMatch(full)) return false;
        var segs = full.Split('.');
        for (int k = segs.Length - 1; k >= 1; k--)
        {
            var nsPart = string.Join('.', segs.Take(k));
            if (!Belongs(nsPart)) continue;
            var reb = RebaseName(nsPart, side);
            if (!Exists(reb))
            {
                if (side == Side.Windows)
                {
                    var core = RebaseName(nsPart, Side.Core);
                    if (!Exists(core)) continue;
                    reb = core;
                }
                else continue;
            }
            result = reb + "." + string.Join('.', segs.Skip(k));
            return true;
        }
        return false;
    }

    private sealed class Impl : CSharpSyntaxRewriter
    {
        private readonly NamespaceRebaser _o;
        private readonly Side _side;
        public Impl(NamespaceRebaser owner, Side side) { _o = owner; _side = side; }

        public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
        {
            var visited = (CompilationUnitSyntax)base.VisitCompilationUnit(node)!;
            return visited.WithUsings(RebuildUsings(visited.Usings));
        }

        public override SyntaxNode? VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            var visited = (NamespaceDeclarationSyntax)base.VisitNamespaceDeclaration(node)!;
            var newName = _o.RebaseName(node.Name.ToString(), _side);
            return visited
                .WithName(SyntaxFactory.ParseName(newName).WithTriviaFrom(node.Name))
                .WithUsings(RebuildUsings(visited.Usings));
        }

        public override SyntaxNode? VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
        {
            var visited = (FileScopedNamespaceDeclarationSyntax)base.VisitFileScopedNamespaceDeclaration(node)!;
            var newName = _o.RebaseName(node.Name.ToString(), _side);
            return visited
                .WithName(SyntaxFactory.ParseName(newName).WithTriviaFrom(node.Name))
                .WithUsings(RebuildUsings(visited.Usings));
        }

        public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node)
        {
            // Only the outermost qualified name; names inside usings/namespace declarations are handled apart.
            if (node.Parent is QualifiedNameSyntax) return node;
            if (node.FirstAncestorOrSelf<UsingDirectiveSyntax>() is not null) return node;
            if (node.Parent is BaseNamespaceDeclarationSyntax) return node;
            if (_o.TryRebaseDotted(node.ToString(), _side, out var reb))
                return SyntaxFactory.ParseName(reb).WithTriviaFrom(node);
            return node;
        }

        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            if (node.Parent is MemberAccessExpressionSyntax) return base.VisitMemberAccessExpression(node);
            var text = node.ToString();
            if (_o.TryRebaseDotted(text, _side, out var reb))
                return SyntaxFactory.ParseExpression(reb).WithTriviaFrom(node);
            return base.VisitMemberAccessExpression(node);
        }

        /// <summary>Rebases a using list, expanding a plain using to the new namespace(s) that exist and
        /// dropping ones whose (split) namespace no longer exists.</summary>
        private SyntaxList<UsingDirectiveSyntax> RebuildUsings(SyntaxList<UsingDirectiveSyntax> usings)
        {
            if (usings.Count == 0) return usings;
            var result = new List<UsingDirectiveSyntax>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var u in usings)
            {
                if (u.Name is null) { result.Add(u); continue; }
                var name = u.Name.ToString();
                bool plain = u.Alias is null && !u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword);
                List<string> targets = plain
                    ? _o.UsingCandidates(name, _side)
                    : new List<string> { _o.ResolveSingle(name, _side) };

                if (targets.Count == 0) continue; // split namespace no longer exists -> drop the using
                bool first = true;
                foreach (var t in targets)
                {
                    var key = (u.Alias?.ToString() ?? string.Empty) + "|" + (u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) ? "static|" : string.Empty) + t;
                    if (!seen.Add(key)) continue;
                    // WithName preserves the directive's trivia (indentation + trailing newline) and keeps the
                    // alias/static/global keywords; extra expansions clone the same shape.
                    var dir = (first ? u : u).WithName(SyntaxFactory.ParseName(t));
                    result.Add(dir);
                    first = false;
                }
            }
            return SyntaxFactory.List(result);
        }
    }
}
