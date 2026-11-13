using Microsoft.AspNetCore.Mvc;

namespace Globly.Api.Common;

/// <summary>
/// Machine-readable error codes sent as the "code" field of ProblemDetails responses.
/// The app maps these to translated messages; "detail" stays as an English fallback.
/// </summary>
public static class ErrorCodes
{
    public const string UsernameTaken = "username_taken";
    public const string InvalidCredentials = "invalid_credentials";
    public const string LockedOut = "locked_out";
    public const string CaptchaFailed = "captcha_failed";
    public const string ContentRejected = "content_rejected";
    public const string InvalidFile = "invalid_file";
    public const string FileTooLarge = "file_too_large";
    public const string FileRequired = "file_required";
    public const string TextRequired = "text_required";
    public const string CommentEmpty = "comment_empty";
    public const string CannotDeleteSelf = "cannot_delete_self";
    public const string CaptureInvalid = "capture_invalid";
    public const string CaptureExpired = "capture_expired";
    public const string CaptureUnverified = "capture_unverified";
}

public static class ProblemExtensions
{
    public static ObjectResult ProblemWithCode(this ControllerBase controller, int status, string code, string detail)
    {
        var result = controller.Problem(detail, statusCode: status);
        if (result.Value is ProblemDetails problem) problem.Extensions["code"] = code;
        return result;
    }
}
