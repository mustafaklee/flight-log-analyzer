using FlightLog.Api.Infrastructure;
using FlightLog.Application.Flights;
using FlightLog.Application.Logs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace FlightLog.Api.Controllers;

/// <summary>Log upload, download and processing state.</summary>
[ApiController]
[Authorize]
[Route("api/v1")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
public sealed class LogFilesController(ILogFileService logFiles, IOptions<UploadOptions> uploadOptions) : ControllerBase
{
    private const string FileFieldName = "file";
    private const long MultipartOverheadBytes = 64 * 1024;

    /// <summary>
    /// Uploads one log file (<c>multipart/form-data</c>, field <c>file</c>) into the project and creates a flight for it.
    /// The body is streamed: it is never buffered in memory. To upload several files, send one request per file.
    /// The format is detected from the content; the extension must agree (<c>.ulg</c> or <c>.bin</c>).
    /// </summary>
    /// <param name="projectId">Target project.</param>
    /// <param name="aircraftId">Optional aircraft of the same team.</param>
    /// <param name="name">Optional flight name; defaults to the file name.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpPost("projects/{projectId:guid}/logs")]
    [DisableFormValueModelBinding]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    [ProducesResponseType<UploadLogResult>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<UploadLogResult>> Upload(Guid projectId, [FromQuery] Guid? aircraftId, [FromQuery] string? name, CancellationToken cancellationToken)
    {
        var sizeFeature = HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = uploadOptions.Value.MaxFileSizeBytes + MultipartOverheadBytes;
        }

        if (!MediaTypeHeaderValue.TryParse(Request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value is not { Length: > 0 } boundary)
        {
            return BadRequestProblem("upload.not_multipart", "Send the file as multipart/form-data in a field named 'file'.");
        }

        var reader = new MultipartReader(boundary, Request.Body);
        while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                || !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(HeaderUtilities.RemoveQuotes(disposition.Name).Value, FileFieldName, StringComparison.Ordinal))
            {
                continue;
            }

            var fileName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value;
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return BadRequestProblem("upload.missing_file_name", "The 'file' field must carry a file name.");
            }

            var result = await logFiles.UploadAsync(projectId, new UploadLogRequest(fileName, aircraftId, name), section.Body, cancellationToken);
            return CreatedAtAction(nameof(Get), new { logFileId = result.LogFile.Id }, result);
        }

        return BadRequestProblem("upload.missing_file", "No form field named 'file' was found.");
    }

    [HttpGet("log-files/{logFileId:guid}")]
    public Task<LogFileDto> Get(Guid logFileId, CancellationToken cancellationToken) =>
        logFiles.GetAsync(logFileId, cancellationToken);

    /// <summary>Downloads the original log file.</summary>
    [HttpGet("log-files/{logFileId:guid}/content")]
    [ProducesResponseType<FileStreamResult>(StatusCodes.Status200OK, "application/octet-stream")]
    public async Task<IActionResult> Download(Guid logFileId, CancellationToken cancellationToken)
    {
        var content = await logFiles.OpenContentAsync(logFileId, cancellationToken);
        Response.ContentLength = content.SizeBytes;
        return File(content.Content, "application/octet-stream", content.FileName);
    }

    /// <summary>Cancels processing of a queued or running file.</summary>
    [HttpPost("log-files/{logFileId:guid}/cancel")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<LogFileDto> Cancel(Guid logFileId, CancellationToken cancellationToken) =>
        logFiles.CancelAsync(logFileId, cancellationToken);

    /// <summary>Queues a failed or cancelled file again.</summary>
    [HttpPost("log-files/{logFileId:guid}/retry")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<LogFileDto> Retry(Guid logFileId, CancellationToken cancellationToken) =>
        logFiles.RetryAsync(logFileId, cancellationToken);

    private ObjectResult BadRequestProblem(string code, string detail)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, StatusCodes.Status400BadRequest, "Invalid upload", detail: detail);
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status400BadRequest, ContentTypes = { "application/problem+json" } };
    }
}
