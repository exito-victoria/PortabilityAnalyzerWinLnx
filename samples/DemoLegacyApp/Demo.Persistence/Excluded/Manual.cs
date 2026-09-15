// Fichero EXCLUIDO de la compilación mediante <Compile Remove="Excluded\**\*.cs" /> en el .csproj.
// Contiene a propósito una referencia a un tipo inexistente: si el reescritor lo incluyera por error,
// el proyecto generado NO compilaría. Como MSBuild lo excluye, el reescritor también debe omitirlo.
namespace Demo.Persistence.Excluded;

public static class Manual
{
    public static void Broken() => TipoQueNoExiste.Hacer();
}
