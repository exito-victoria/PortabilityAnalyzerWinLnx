using System.Diagnostics;

namespace Demo.Persistence;

/// <summary>Escribe eventos de auditoría en el Visor de eventos de Windows. DEPENDENCIA WINDOWS:
/// EventLog es exclusivo de Windows; debería sustituirse por un logging portable.</summary>
public sealed class EventLogAuditor
{
    private const string Source = "DemoLegacyApp";

    public void Audit(string message)
    {
        if (!EventLog.SourceExists(Source))
            EventLog.CreateEventSource(Source, "Application");

        using var log = new EventLog("Application") { Source = Source };
        log.WriteEntry(message, EventLogEntryType.Information);
    }
}
