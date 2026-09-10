namespace Demo.Persistence;

/// <summary>Servicio de configuración PORTABLE en sí mismo (no usa ninguna API de Windows), pero
/// depende de <see cref="RegistrySettings"/>, que sí es de Windows. Es el caso típico de referencia
/// cruzada núcleo→Windows: el reescritor debe extraer un seam (interfaz) e inyectarlo, para que este
/// servicio se quede en el núcleo portable.</summary>
public sealed class SettingsService
{
    private readonly RegistrySettings _settings = new();

    public string CurrentConnection() => _settings.GetConnectionString();

    public void UpdateConnection(string value) => _settings.SetConnectionString(value);
}
