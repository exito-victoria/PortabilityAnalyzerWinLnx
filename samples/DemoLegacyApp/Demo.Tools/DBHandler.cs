using Demo.DataAccess;

namespace Demo.Tools;

/// <summary>Tiene un CAMPO PÚBLICO de un tipo de Windows de OTRO proyecto (GenericDataAccess). No es
/// inyectable por seam (campo público, sin patrón new privado): debe acabar en Demo.Tools.Windows.</summary>
public sealed class DBHandler
{
    public GenericDataAccess gda = new GenericDataAccess();

    public int Run(string sql) => gda.Execute(sql);
}
