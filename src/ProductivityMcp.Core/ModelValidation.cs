using System.ComponentModel.DataAnnotations;

namespace ProductivityMcp.Core;

public static class ModelValidation
{
    public static OperationResult<T> Validate<T>(T? value, string name)
        where T : class
    {
        if (value is null)
        {
            return OperationResult.Fail<T>(new OperationError(
                OperationErrorCode.Validation,
                $"{name} is required.",
                name));
        }

        var failures = new List<ValidationResult>();
        if (Validator.TryValidateObject(
                value,
                new ValidationContext(value),
                failures,
                validateAllProperties: true))
        {
            return OperationResult.Ok(value);
        }

        var failure = failures[0];
        var field = failure.MemberNames.FirstOrDefault();
        return OperationResult.Fail<T>(new OperationError(
            OperationErrorCode.Validation,
            failure.ErrorMessage ?? $"{name} is invalid.",
            field is null ? name : ToCamelCase(field)));
    }

    public static OperationResult<string> RequiredId(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return OperationResult.Fail<string>(new OperationError(
                OperationErrorCode.Validation,
                $"{name} must not be empty.",
                name));
        }

        return OperationResult.Ok(value);
    }

    private static string ToCamelCase(string value) =>
        string.IsNullOrEmpty(value) || char.IsLower(value[0])
            ? value
            : char.ToLowerInvariant(value[0]) + value[1..];
}
