namespace Demo.Contracts;

/// <summary>Contrato portable compartido, en una librería EXTERNA a la solución (net8.0). Sirve para
/// validar que el reescritor conserva las ProjectReference a proyectos de fuera de la solución.</summary>
public interface IEntity
{
    string Id { get; }
}
