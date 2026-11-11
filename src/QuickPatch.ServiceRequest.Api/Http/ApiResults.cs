namespace QuickPatch.ServiceRequest.Api.Http;

/// <summary>
/// Respuestas HTTP comunes a los endpoints (Problem Details, RFC 9457) y lectura de la paginación. Antes vivían en
/// <c>ServiceRequestEndpoints</c> y hacían que los demás grupos de endpoints dependieran de él (SCRUM-652).
/// </summary>
public static class ApiResults
{
    /// <summary>401 cuando el token no trae usuario ni tenant válidos.</summary>
    public static IResult InvalidToken() =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");

    public static IResult AddressNotFound() => Results.Problem(
        statusCode: StatusCodes.Status422UnprocessableEntity,
        type: Problems.AddressNotFound,
        title: "No encontramos la dirección",
        detail: "Comparte tu ubicación para crear la solicitud.");

    public static int? ParseOptionalInt(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (value is null)
        {
            return null;
        }

        if (int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        errors[field] = ["Debe ser un número entero."];
        return null;
    }

    public static IResult ValidationProblem(IReadOnlyDictionary<string, string[]> errors) =>
        Results.ValidationProblem(
            errors.ToDictionary(e => e.Key, e => e.Value),
            title: "La solicitud tiene datos inválidos",
            type: Problems.Validation);
}