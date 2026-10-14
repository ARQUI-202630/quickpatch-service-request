namespace QuickPatch.ServiceRequest.Domain.ServiceRequests;

/// <summary>Resultado de aplicar un evento de Matching a la solicitud.</summary>
public enum MatchingEventOutcome
{
    /// <summary>La solicitud cambió de estado.</summary>
    Applied,

    /// <summary>El evento es reciente, pero la solicitud no está en el estado de origen; el estado no cambia.</summary>
    Ignored,

    /// <summary>Ya se aplicó un evento de Matching más reciente; este se descarta.</summary>
    Stale,
}