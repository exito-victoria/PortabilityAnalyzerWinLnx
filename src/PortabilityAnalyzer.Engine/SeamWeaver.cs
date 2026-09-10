using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PortabilityAnalyzer.Engine;

/// <summary>
/// Teje "seams" (costuras) automáticos cuando un fichero PORTABLE del núcleo depende de una clase que
/// quedó en el proyecto Windows. En vez de arrastrar el consumidor a Windows, EXTRAE una interfaz de la
/// clase Windows (`I{Tipo}`), la coloca en el núcleo, hace que la clase Windows la implemente y reescribe
/// el consumidor para recibir la interfaz por INYECCIÓN DE DEPENDENCIAS (constructor). Así el núcleo se
/// mantiene portable y compilable, y la implementación Windows se conecta por DI en el arranque.
/// Transformación conservadora: solo actúa sobre el patrón campo `private ... T _x = new T(...);` en clases
/// SIN constructor propio; si no encaja, devuelve null y el reescritor deja el fichero en Windows.
/// </summary>
internal static class SeamWeaver
{
    /// <summary>Un seam generado: la interfaz portable extraída de una clase Windows.</summary>
    public sealed record SeamPlan(string ConcreteType, string InterfaceName, string Namespace, string InterfaceSource);

    /// <summary>Miembros públicos de instancia de un tipo (para extraer su interfaz).</summary>
    private sealed record Member(string Text);

    /// <summary>Extrae la interfaz de un tipo Windows a partir del contenido de su fichero. Devuelve null
    /// si no se encuentra el tipo o no tiene miembros públicos de instancia.</summary>
    public static SeamPlan? ExtractInterface(string winFileContent, string typeName)
    {
        SyntaxNode root;
        try { root = CSharpSyntaxTree.ParseText(winFileContent).GetRoot(); }
        catch { return null; }

        var type = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == typeName && t is ClassDeclarationSyntax);
        if (type is null) return null;

        var ns = type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? string.Empty;
        var members = PublicInstanceMembers(type);
        if (members.Count == 0) return null;

        var interfaceName = "I" + typeName;
        var sb = new StringBuilder();
        sb.AppendLine($"// [Reescritura multiplataforma] Seam portable: interfaz extraída de {typeName} (que usa API de Windows).");
        sb.AppendLine("// El núcleo depende de esta interfaz; el proyecto Windows aporta la implementación (inyección de dependencias).");
        if (!string.IsNullOrEmpty(ns)) { sb.AppendLine($"namespace {ns};"); sb.AppendLine(); }
        sb.AppendLine($"public interface {interfaceName}");
        sb.AppendLine("{");
        foreach (var m in members) sb.AppendLine("    " + m.Text);
        sb.AppendLine("}");
        return new SeamPlan(typeName, interfaceName, ns, sb.ToString());
    }

    private static List<Member> PublicInstanceMembers(TypeDeclarationSyntax type)
    {
        var result = new List<Member>();
        foreach (var m in type.Members)
        {
            bool isPublic = m.Modifiers.Any(x => x.IsKind(SyntaxKind.PublicKeyword));
            bool isStatic = m.Modifiers.Any(x => x.IsKind(SyntaxKind.StaticKeyword));
            if (!isPublic || isStatic) continue;

            switch (m)
            {
                case MethodDeclarationSyntax method:
                    result.Add(new Member($"{method.ReturnType.ToString().Trim()} {method.Identifier.Text}{method.ParameterList.ToString().Trim()};"));
                    break;
                case PropertyDeclarationSyntax prop:
                    var acc = prop.AccessorList?.Accessors;
                    bool hasGet = acc?.Any(a => a.Keyword.IsKind(SyntaxKind.GetKeyword)) ?? false;
                    bool hasSet = acc?.Any(a => a.Keyword.IsKind(SyntaxKind.SetKeyword) || a.Keyword.IsKind(SyntaxKind.InitKeyword)) ?? false;
                    if (!hasGet && !hasSet) hasGet = true;
                    var body = hasGet && hasSet ? "{ get; set; }" : hasGet ? "{ get; }" : "{ set; }";
                    result.Add(new Member($"{prop.Type.ToString().Trim()} {prop.Identifier.Text} {body}"));
                    break;
            }
        }
        return result;
    }

    /// <summary>Añade la interfaz a la lista de bases de la clase Windows (para que la implemente). Como la
    /// interfaz vive en el mismo namespace que el tipo, no hacen falta usings nuevos.</summary>
    public static string AddBaseInterface(string winFileContent, string typeName, string interfaceName)
    {
        try
        {
            var root = CSharpSyntaxTree.ParseText(winFileContent).GetRoot();
            var cls = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == typeName);
            if (cls is null) return winFileContent;
            // Si ya implementa la interfaz, no tocar.
            if (cls.BaseList is not null && cls.BaseList.Types.Any(t => t.Type.ToString() == interfaceName)) return winFileContent;

            // Inserción por posición de texto para preservar el formato original (evita colapsar la llave).
            if (cls.BaseList is null)
            {
                var pos = (cls.TypeParameterList?.Span.End) ?? cls.Identifier.Span.End;
                return winFileContent[..pos] + " : " + interfaceName + winFileContent[pos..];
            }
            else
            {
                var pos = cls.BaseList.Span.End;
                return winFileContent[..pos] + ", " + interfaceName + winFileContent[pos..];
            }
        }
        catch { return winFileContent; }
    }

    /// <summary>Intenta reescribir un fichero del núcleo que usa tipos Windows: cada campo
    /// `private ... T _x = new T(...);` cuyo T tenga interfaz se convierte en el tipo interfaz y se inyecta
    /// por constructor. Solo actúa en clases SIN constructor de instancia propio. Devuelve el contenido
    /// reescrito, o null si el patrón no encaja (el reescritor entonces deja el fichero en Windows).</summary>
    public static string? TryInjectConstructor(string coreFileContent, IReadOnlyDictionary<string, string> concreteToInterface)
    {
        SyntaxNode root;
        try { root = CSharpSyntaxTree.ParseText(coreFileContent).GetRoot(); }
        catch { return null; }

        // Comprobar que TODAS las referencias a tipos Windows en este fichero son campos inyectables.
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
        // Mapa: clase -> lista de (campo, tipoConcreto, interfaz, nombreCampo)
        var plansByClass = new Dictionary<ClassDeclarationSyntax, List<(FieldDeclarationSyntax Field, string Concrete, string Interface, string FieldName)>>();

        // Recolectar todos los usos de tipos Windows como nombres de tipo.
        foreach (var cls in classes)
        {
            var plans = new List<(FieldDeclarationSyntax, string, string, string)>();
            foreach (var field in cls.Members.OfType<FieldDeclarationSyntax>())
            {
                var typeName = StripGlobal(field.Declaration.Type.ToString().Trim());
                if (!concreteToInterface.TryGetValue(typeName, out var iface)) continue;
                // Debe declarar una sola variable con inicializador 'new T(...)' o 'new()'.
                if (field.Declaration.Variables.Count != 1) return null;
                var v = field.Declaration.Variables[0];
                if (v.Initializer is null) return null;
                var init = v.Initializer.Value;
                bool isNewOfType = init is ObjectCreationExpressionSyntax oc && StripGlobal(oc.Type.ToString().Trim()) == typeName;
                bool isImplicitNew = init is ImplicitObjectCreationExpressionSyntax;
                if (!isNewOfType && !isImplicitNew) return null;
                plans.Add((field, typeName, iface, v.Identifier.Text));
            }
            if (plans.Count > 0)
            {
                // Si la clase ya tiene un constructor de instancia, no la tocamos (fallback).
                if (cls.Members.OfType<ConstructorDeclarationSyntax>().Any()) return null;
                plansByClass[cls] = plans;
            }
        }

        if (plansByClass.Count == 0) return null;

        // Verificar que NO queden otros usos de tipos Windows fuera de esos campos (p. ej. 'new T()' en
        // el cuerpo de un método), que no sabríamos inyectar de forma segura.
        var handledFieldTypes = plansByClass.Values.SelectMany(p => p.Select(x => x.Field)).ToHashSet();
        foreach (var oc in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var t = StripGlobal(oc.Type.ToString().Trim());
            if (!concreteToInterface.ContainsKey(t)) continue;
            // ¿Está dentro de un inicializador de campo que vamos a tratar? Si no, es un uso no soportado.
            var inHandledField = oc.Ancestors().OfType<FieldDeclarationSyntax>().Any(f => handledFieldTypes.Contains(f));
            if (!inHandledField) return null;
        }

        // Reescritura por clase: cambiar el tipo de cada campo por la interfaz, quitar el inicializador y
        // añadir un constructor que inyecte las interfaces.
        var newRoot = root.ReplaceNodes(plansByClass.Keys, (origCls, _) =>
        {
            var plans = plansByClass[origCls];

            // 1) Reemplazar cada campo: tipo -> interfaz, sin inicializador.
            var cls = origCls.ReplaceNodes(plans.Select(p => p.Field), (of, _) =>
            {
                var plan = plans.First(p => p.Field == of);
                var newDecl = SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.ParseTypeName(plan.Interface + " "),
                    SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(plan.FieldName)));
                return of.WithDeclaration(newDecl);
            });

            // 2) Construir e insertar el constructor.
            var ctorName = origCls.Identifier.Text;
            var parms = plans.Select(p => $"{p.Interface} {ArgName(p.FieldName)}");
            var assigns = plans.Select(p => $"        this.{p.FieldName} = {ArgName(p.FieldName)};");
            var ctorText =
                $"    public {ctorName}({string.Join(", ", parms)})\r\n" +
                "    {\r\n" +
                string.Join("\r\n", assigns) + "\r\n" +
                "    }\r\n";
            var ctor = SyntaxFactory.ParseMemberDeclaration(ctorText);
            if (ctor is null) return cls;

            // Insertar el constructor como primer miembro (tras los campos ya reemplazados si se prefiere;
            // como primer miembro es válido y legible).
            // El texto del constructor ya trae su sangría (4 espacios) como trivia inicial; la llave de
            // apertura de la clase aporta el salto de línea previo. No sobrescribir la trivia (evita
            // líneas en blanco y desindentado).
            var members = cls.Members.Insert(0, ctor);
            return cls.WithMembers(members);
        });

        return newRoot.ToFullString();
    }

    private static string ArgName(string fieldName)
    {
        var n = fieldName.TrimStart('_');
        return string.IsNullOrEmpty(n) ? "value" : char.ToLowerInvariant(n[0]) + n[1..];
    }

    private static string StripGlobal(string typeName) =>
        typeName.StartsWith("global::", StringComparison.Ordinal) ? typeName["global::".Length..] : typeName;
}
