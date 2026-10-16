using DeviceTelemetry.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace DeviceTelemetry.Api.Http;

public static class DomainErrorResults
{
    /// <summary>
    /// Converts a <see cref="DomainError"/> to an RFC 9457 problem response.
    /// The stable error code is exposed as the <c>code</c> extension member.
    /// </summary>
    public static ObjectResult Problem(this ControllerBase controller, DomainError error)
    {
        var status = error.Type switch
        {
            DomainErrorType.Validation => StatusCodes.Status400BadRequest,
            DomainErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            DomainErrorType.Forbidden => StatusCodes.Status403Forbidden,
            DomainErrorType.NotFound => StatusCodes.Status404NotFound,
            DomainErrorType.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError,
        };

        var problem = controller.ProblemDetailsFactory.CreateProblemDetails(
            controller.HttpContext, statusCode: status, detail: error.Message);
        problem.Extensions["code"] = error.Code;

        return new ObjectResult(problem) { StatusCode = status };
    }
}
